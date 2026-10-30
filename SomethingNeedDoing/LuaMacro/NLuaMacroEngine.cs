using NLua;
using SomethingNeedDoing.Core.Events;
using SomethingNeedDoing.Core.Interfaces;
using SomethingNeedDoing.LuaMacro.Modules;
using SomethingNeedDoing.LuaMacro.Modules.Engines;
using SomethingNeedDoing.Managers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SomethingNeedDoing.LuaMacro;

/// <summary>
/// Executes Lua script macros using NLua.
/// </summary>
[RegisterSingleton, AutoConstruct]
public partial class NLuaMacroEngine : IMacroEngine
{
    private readonly LuaModuleManager _moduleManager;
    private readonly CleanupManager _cleanupManager;
    private readonly MacroHierarchyManager _macroHierarchy;

    /// <inheritdoc/>
    public event EventHandler<MacroErrorEventArgs>? MacroError;

    /// <inheritdoc/>
    public event EventHandler<MacroControlEventArgs>? MacroControlRequested;

    /// <inheritdoc/>
    public event EventHandler<MacroStepCompletedEventArgs>? MacroStepCompleted;

    /// <inheritdoc/>
    public event EventHandler<MacroExecutionRequestedEventArgs>? MacroExecutionRequested;

    /// <summary>
    /// Event raised when loop control is requested.
    /// </summary>
    public event EventHandler<LoopControlEventArgs>? LoopControlRequested;

    private readonly ConcurrentDictionary<string, TemporaryMacro> _temporaryMacros = [];
    private readonly ConcurrentDictionary<string, Lua> _activeLuaEnvironments = [];
    private readonly ConcurrentDictionary<string, LuaCrashDiagnostics> _crashDiagnostics = [];
    private volatile bool _isShuttingDown;
    private readonly Lazy<LuaCrashLog> _crashLog = new(() => new LuaCrashLog(
        System.IO.Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "diagnostics")));

    internal void TraceLua(string macroId, string phase)
    {
        if (_crashDiagnostics.TryGetValue(macroId, out var diagnostics)) diagnostics.Record(phase);
    }

    internal void BeginShutdown() => _isShuttingDown = true;

    internal void TraceLifecycle(string reason)
    {
        if (!LuaCrashDiagnostics.Enabled) return;
        try
        {
            var context = $"LIFECYCLE {reason} loggedIn={Svc.ClientState.IsLoggedIn} territory={Svc.ClientState.TerritoryType} " +
                $"betweenAreas={Svc.Condition[ConditionFlag.BetweenAreas]} betweenAreas51={Svc.Condition[ConditionFlag.BetweenAreas51]}";
            _crashLog.Value.Write(context + $" activeLua={_crashDiagnostics.Count}");
            foreach (var diagnostics in _crashDiagnostics.Values) diagnostics.Record(context);
        }
        catch (Exception ex)
        {
            FrameworkLogger.Warning($"Could not record Lua lifecycle diagnostics: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public IMacro? GetTemporaryMacro(string macroId) => _temporaryMacros.TryGetValue(macroId, out var macro) ? macro : null;
    public Lua? GetLuaEnvironment(string macroId) => _activeLuaEnvironments.TryGetValue(macroId, out var lua) ? lua : null;

    /// <summary>
    /// Represents the current state of a macro execution.
    /// </summary>
    private class MacroInstance(IMacro macro)
    {
        public IMacro Macro { get; } = macro;
        public LuaFunction? LuaGenerator { get; set; }
    }

    /// <inheritdoc/>
    public async Task StartMacro(IMacro macro, IMacroInstance instance, CancellationToken token, TriggerEventArgs? triggerArgs = null, int _ = 0)
    {
        if (macro.Type != MacroType.Lua)
            throw new ArgumentException("This engine only supports Lua macros", nameof(macro));

        if (macro is not TemporaryMacro)
            _cleanupManager.RegisterCleanupFunctions(macro);

        var state = new MacroInstance(macro);

        try
        {
            await ExecuteMacro(state, instance, token, triggerArgs);
        }
        catch (Exception ex)
        {
            OnMacroError(macro, "Macro execution failed", ex);
            throw;
        }
    }

    private async Task ExecuteMacro(MacroInstance macro, IMacroInstance instance, CancellationToken token, TriggerEventArgs? triggerArgs = null, int _ = 0)
    {
        Lua? lua = null;
        LuaCrashDiagnostics? diagnostics = null;
        try
        {
            FrameworkLogger.Verbose($"Starting Lua macro execution for macro {macro.Macro.Id}");
            lua = new Lua();
            lua.State.Encoding = Encoding.UTF8;
            if (LuaCrashDiagnostics.Enabled)
            {
                try
                {
                    diagnostics = new LuaCrashDiagnostics(lua, macro.Macro.Id, macro.Macro.Name, _crashLog.Value.Write);
                    _crashDiagnostics[macro.Macro.Id] = diagnostics;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(macro.Macro.ContentSansMetadata())));
                    diagnostics.Record($"SETUP trigger={triggerArgs?.EventType.ToString() ?? "manual"} scriptSha256={hash} entrypointLineOffset=4");
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Warning($"Could not attach Lua crash diagnostics: {ex.Message}");
                }
            }

            lua.LoadCLRPackage();
            lua.LoadFStrings();
            lua.LoadPackageSearcherSnippet();
            lua.LoadRequirePaths();
            lua.LoadErrorHandler();
            lua.ApplyPrintOverride();
            lua.RegisterInternalFunctions();
            lua.SetTriggerEventData(triggerArgs);
            lua.RegisterClass<Svc>();
            _moduleManager.RegisterAll(lua);

            var nativeEngine = new NativeEngine();
            nativeEngine.MacroExecutionRequested += (sender, e) =>
                MacroExecutionRequested?.Invoke(this, e);

            nativeEngine.LoopControlRequested += (sender, e) =>
                LoopControlRequested?.Invoke(this, e);

            var luaEngine = new LuaEngine();
            luaEngine.MacroExecutionRequested += (sender, e) =>
                MacroExecutionRequested?.Invoke(this, e);

            var engines = new List<IEngine>
            {
                nativeEngine,
                luaEngine
            };

            var enginesModule = new EnginesModule(engines);
            enginesModule.Register(lua);
            new ConfigModule(macro.Macro).Register(lua);

            _activeLuaEnvironments[macro.Macro.Id] = lua; // for function triggers to access the same state

            diagnostics?.Begin("dependencies");
            await LoadDependenciesIntoScope(lua, macro.Macro);
            diagnostics?.End("dependencies");
            await Svc.Framework.RunOnTick(async () =>
            {
                try
                {
                    // Execute the script
                    token.ThrowIfCancellationRequested();
                    diagnostics?.Begin("load-entrypoint");
                    var results = lua.LoadEntryPointWrappedScript(macro.Macro.ContentSansMetadata(), $"@snd/{macro.Macro.Id}/entrypoint");
                    diagnostics?.End("load-entrypoint");
                    if (results.Length == 0 || results[0] is not LuaFunction func)
                        throw new LuaException("Failed to load Lua script: No function returned");

                    macro.LuaGenerator = func;

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            // Wait if paused
                            await MacroPause.WaitAsync(instance.PauseEvent, token);

                            if (macro.LuaGenerator == null)
                                break;

                            var (macroComplete, result) = await Svc.Framework.RunOnTick(() =>
                            {
                                diagnostics?.Begin("resume");
                                var result = macro.LuaGenerator.Call();
                                diagnostics?.End($"resume results={result.Length}");
                                return (result.Length == 0, result);
                            }, cancellationToken: token);

                            token.ThrowIfCancellationRequested();

                            if (macroComplete)
                                break;

                            if (result.First() is not string text)
                            {
                                var valueType = result.First()?.GetType().Name ?? "null";
                                var valueStr = result.First()?.ToString() ?? "null";
                                throw new MacroException($"Lua Macro yielded a non-string value [{valueType}: {valueStr}]");
                            }

                            if (MacroExecutionRequested is { })
                            {
                                var tempId = $"{macro.Macro.Id}_native_{Guid.NewGuid()}";
                                var tempMacro = new TemporaryMacro(macro.Macro, text, _macroHierarchy, tempId);
                                _temporaryMacros[tempId] = tempMacro;
                                await tempMacro.Run(MacroExecutionRequested);
                                _temporaryMacros.Remove(tempId, out var _);
                            }

                            MacroStepCompleted?.Invoke(this, new MacroStepCompletedEventArgs(macro.Macro.Id, 1, 1));

                            await Svc.Framework.RunOnTick(() => true, cancellationToken: token);
                        }
                        catch (OperationCanceledException)
                        {
                            FrameworkLogger.Debug($"Operation cancelled for macro {macro.Macro.Id}");
                            break;
                        }
                        catch (LuaException ex)
                        {
                            OnMacroError(macro.Macro, $"Error executing Lua function for macro {macro.Macro.Id}", ex);
                            break;
                        }
                        catch (Exception ex)
                        {
                            var errorDetails = "Unknown error";
                            OnMacroError(macro.Macro, $"Error executing Lua function for macro {macro.Macro.Id}: {errorDetails}", ex);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    FrameworkLogger.Debug($"Operation cancelled for macro {macro.Macro.Id}");
                }
                catch (Exception ex)
                {
                    OnMacroError(macro.Macro, "Error executing macro", ex);
                }
                finally
                {
                    diagnostics?.Begin("cleanup");
                    if (!_isShuttingDown)
                        await ExecuteCleanupFunctions(lua, macro.Macro, diagnostics);
                    diagnostics?.End("cleanup");
                }
            }, cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FrameworkLogger.Info($"Operation cancelled for macro {macro.Macro.Id}");
        }
        catch (Exception ex)
        {
            OnMacroError(macro.Macro, "Error executing macro", ex);
            throw;
        }
        finally
        {
            _activeLuaEnvironments.Remove(macro.Macro.Id, out var _);

            // Doing this manually since NLua is a very well written library
            if (lua != null)
            {
                try
                {
                    diagnostics?.Begin("dispose");
                    lua.Dispose();
                    diagnostics?.Record("END dispose");
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Warning($"Error disposing Lua environment for macro {macro.Macro.Id}: {ex.Message}");
                }
            }

            // The hook delegate must stay rooted until lua_close has finished running __gc.
            GC.KeepAlive(diagnostics);
            _crashDiagnostics.TryRemove(macro.Macro.Id, out var removedDiagnostics);

            macro.LuaGenerator = null;
        }
    }

    protected virtual void OnMacroError(IMacro macro, string message, Exception? ex = null)
    {
        TraceLua(macro.Id, $"ERROR {message} exception={ex?.GetType().FullName} message={ex?.Message}");
        Svc.Chat.PrintErrorMsg(message);
        FrameworkLogger.Error($"Error executing macro {macro.Name} [{macro.Id}]: {ex}");
        MacroError?.Invoke(this, new MacroErrorEventArgs(macro.Id, message, ex));
    }

    /// <summary>
    /// Loads all dependencies into the Lua scope.
    /// </summary>
    /// <param name="lua">The Lua state.</param>
    /// <param name="macro">The macro whose dependencies to load.</param>
    private async Task LoadDependenciesIntoScope(Lua lua, IMacro macro)
    {
        if (macro.Metadata.Dependencies.Count == 0)
            return;

        FrameworkLogger.Debug($"Loading {macro.Metadata.Dependencies.Count} dependencies for macro {macro.Name}");

        foreach (var dependency in macro.Metadata.Dependencies)
        {
            try
            {
                var content = await dependency.GetContentAsync();
                TraceLua(macro.Id, $"BEGIN dependency={dependency.Name}");
                lua.DoString(content, $"@snd/{macro.Id}/dependency/{dependency.Name}");
                TraceLua(macro.Id, $"END dependency={dependency.Name}");
                FrameworkLogger.Debug($"Loaded dependency {dependency.Name} into Lua scope for macro {macro.Name}");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Error(ex, $"Failed to load dependency {dependency.Name} for macro {macro.Name}");
                throw new MacroException($"Failed to load dependency {dependency.Name}: {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_crashLog.IsValueCreated) _crashLog.Value.Dispose();
    }

    /// <summary>
    /// Executes cleanup functions for a macro.
    /// </summary>
    /// <param name="lua">The Lua environment.</param>
    /// <param name="macro">The macro to execute cleanup for.</param>
    private async Task ExecuteCleanupFunctions(Lua lua, IMacro macro, LuaCrashDiagnostics? diagnostics)
    {
        if (_isShuttingDown || macro is TemporaryMacro) return;

        var cleanupFunctions = GetCleanupFunctions(macro.Id);
        if (cleanupFunctions.Count == 0) return;

        foreach (var functionName in cleanupFunctions)
        {
            if (_isShuttingDown) break;
            try
            {
                var results = lua.LoadEntryPointWrappedScript($@"{functionName}()", $"@snd/{macro.Id}/cleanup/{functionName}");
                if (results.Length == 0 || results[0] is not LuaFunction func)
                {
                    FrameworkLogger.Error($"Failed to load cleanup function {functionName}");
                    continue;
                }

                try
                {
                    // TODO: don't duplicate logic from executemacro?
                    while (true)
                    {
                        if (_isShuttingDown) break;
                        if (func == null)
                        {
                            FrameworkLogger.Debug($"Cleanup function {functionName} completed (func is null)");
                            break;
                        }

                        diagnostics?.Begin($"cleanup-function={functionName}");
                        var result = func.Call();
                        diagnostics?.End($"cleanup-function={functionName} results={result.Length}");
                        if (result.Length == 0)
                        {
                            FrameworkLogger.Debug($"Cleanup function {functionName} completed");
                            break;
                        }

                        if (result[0] is string command && MacroExecutionRequested is { })
                        {
                            var tempMacro = new TemporaryMacro(macro, command, _macroHierarchy, $"{macro.Id}_cleanup_cmd_{Guid.NewGuid()}")
                            {
                                Name = $"{macro.Name} - Cleanup Command",
                                Type = MacroType.Native
                            };

                            await tempMacro.Run(MacroExecutionRequested);
                        }
                        else
                        {
                            FrameworkLogger.Warning($"Cleanup function {functionName} completed with non-string result");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Error($"Error during cleanup function {functionName} execution: {ex}");
                }
            }
            catch (Exception ex)
            {
                FrameworkLogger.Error($"Error executing cleanup function {functionName} for macro {macro.Name}: {ex}");
            }
        }

        if (macro is not TemporaryMacro)
        {
            _cleanupManager.UnregisterCleanupFunctions(macro);
            FrameworkLogger.Debug($"Unregistered cleanup functions for macro {macro.Name} after execution");
        }
    }

    /// <summary>
    /// Gets cleanup functions for a macro from the cleanup manager.
    /// </summary>
    /// <param name="macroId">The macro ID.</param>
    /// <returns>List of cleanup function names.</returns>
    private List<string> GetCleanupFunctions(string macroId)
        => _cleanupManager.HasCleanupFunctions(macroId) ? [.. _cleanupManager.GetCleanupFunctions(macroId)] : [];
}

/// <summary>
/// Exception thrown by Lua code.
/// </summary>
public class LuaException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LuaException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public LuaException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LuaException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public LuaException(string message, Exception innerException) : base(message, innerException) { }
}
