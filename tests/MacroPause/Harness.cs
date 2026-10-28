using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SomethingNeedDoing.Utils;

namespace SndPauseTests
{
    // A single framework thread; RunOnTick uses Dalamud's HideScheduler behavior.
    public sealed class Framework : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> queue = new BlockingCollection<Task>();
        private readonly Thread thread;
        private readonly TaskFactory factory;
        public bool IsFrameworkThread { get { return Thread.CurrentThread == thread; } }
        public Framework()
        {
            factory = new TaskFactory(CancellationToken.None, TaskCreationOptions.None, TaskContinuationOptions.None, this);
            thread = new Thread(() => { foreach (var task in queue.GetConsumingEnumerable()) TryExecuteTask(task); });
            thread.IsBackground = true;
        }
        public void Start() { thread.Start(); }
        public Task<T> RunOnTick<T>(Func<T> action, CancellationToken cancellationToken = default(CancellationToken))
        {
            return factory.ContinueWhenAll(new[] { Task.CompletedTask }, _ => action(),
                cancellationToken, TaskContinuationOptions.HideScheduler, this);
        }
        public Task RunOnTick(Func<Task> action, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunOnTick<Task>(action, cancellationToken).Unwrap();
        }
        public Task DelayTicks(int ticks) { return RunOnTick(() => Task.CompletedTask); }
        protected override void QueueTask(Task task) { queue.Add(task); }
        protected override bool TryExecuteTaskInline(Task task, bool previouslyQueued) { return false; }
        protected override IEnumerable<Task> GetScheduledTasks() { return queue.ToArray(); }
        public void Dispose()
        {
            queue.CompleteAdding();
            if (!thread.Join(2000)) throw new Exception("Fixture scheduler did not exit.");
            queue.Dispose();
        }
    }
    public static class Svc { public static Framework Framework; }
    public static class FrameworkLogger { public static void Debug(string message) { } }
    public sealed class Macro { public string Id = "paused-macro"; }
    public sealed class MacroExecutionState
    {
        public Macro Macro = new Macro();
        public int CurrentLoop;
        public int LoopCount = 1;
        public List<Command> Commands = new List<Command>();
    }
    public sealed class MacroInstance
    {
        public Macro Macro = new Macro();
        public Generator LuaGenerator = new Generator();
    }
    public sealed class Generator
    {
        public int Calls;
        public object[] Call()
        {
            if (!Svc.Framework.IsFrameworkThread) throw new Exception("Lua resumed off the framework thread.");
            Interlocked.Increment(ref Calls);
            return Array.Empty<object>();
        }
    }
    public sealed class IMacroInstance : IDisposable
    {
        public ManualResetEventSlim PauseEvent = new ManualResetEventSlim(true);
        public bool PauseAtLoop;
        public bool StopAtLoop;
        public CancellationTokenSource CancellationSource = new CancellationTokenSource();
        public void Dispose() { PauseEvent.Dispose(); CancellationSource.Dispose(); }
    }
    public sealed class MacroContext
    {
        public MacroContext(Macro macro) { }
        public event EventHandler MacroExecutionRequested { add { } remove { } }
        public event EventHandler LoopControlRequested { add { } remove { } }
        public int CurrentStep;
        public void NextStep() { CurrentStep++; }
    }
    public sealed class Command
    {
        public bool RequiresFrameworkThread { get { return true; } }
        public int Calls;
        public Task Execute(MacroContext context, CancellationToken token)
        {
            if (!Svc.Framework.IsFrameworkThread) throw new Exception("Command ran off the framework thread.");
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }
    public sealed class TemporaryMacro
    {
        public TemporaryMacro(Macro macro, string command, object hierarchy, string id) { }
        public Task Run(EventHandler handler) { return Task.CompletedTask; }
    }
    public sealed class MacroStepCompletedEventArgs : EventArgs
    {
        public MacroStepCompletedEventArgs(string id, int step, int total) { }
    }
    public sealed class MacroGateCompleteException : Exception { }
    public class MacroException : Exception { public MacroException(string message) : base(message) { } }
    public sealed class LuaException : Exception { }
    public sealed class Diagnostics { public void Begin(string phase) { } public void End(string phase) { } }
    public class EngineBase
    {
        public EventHandler MacroExecutionRequested;
        public EventHandler LoopControlRequested;
        public EventHandler<MacroStepCompletedEventArgs> MacroStepCompleted;
        protected object _macroHierarchy;
        protected Dictionary<string, TemporaryMacro> _temporaryMacros = new Dictionary<string, TemporaryMacro>();
        public readonly List<Exception> Errors = new List<Exception>();
        protected void OnMacroError(string id, string message, Exception ex) { Errors.Add(ex); }
        protected void OnMacroError(Macro macro, string message, Exception ex) { Errors.Add(ex); }
    }
    public sealed class NativeEngine : EngineBase { /* NATIVE_LOOP */ }
    public sealed class BaselineNativeEngine : EngineBase { /* BASELINE_NATIVE_LOOP */ }
    public sealed class LuaEngine : EngineBase
    {
        public async Task ExecuteMacro(MacroInstance macro, IMacroInstance instance, CancellationToken token)
        {
            Diagnostics diagnostics = null;
            /* LUA_LOOP */
        }
    }
    public sealed class BaselineLuaEngine : EngineBase
    {
        public async Task ExecuteMacro(MacroInstance macro, IMacroInstance instance, CancellationToken token)
        {
            Diagnostics diagnostics = null;
            /* BASELINE_LUA_LOOP */
        }
    }
    public static class Harness
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void QueuedPause(bool lua, bool baseline, bool stop)
        {
            using (var framework = new Framework())
            using (var instance = new IMacroInstance())
            using (var entered = new ManualResetEventSlim(false))
            {
                Svc.Framework = framework;
                var command = new Command();
                var nativeState = new MacroExecutionState();
                nativeState.Commands.Add(command);
                var luaState = new MacroInstance();
                EngineBase engine = lua
                    ? (baseline ? (EngineBase)new BaselineLuaEngine() : new LuaEngine())
                    : (baseline ? (EngineBase)new BaselineNativeEngine() : new NativeEngine());
                Func<Task> execute;
                if (engine is NativeEngine) execute = () => ((NativeEngine)engine).ExecuteMacro(nativeState, instance, instance.CancellationSource.Token);
                else if (engine is BaselineNativeEngine) execute = () => ((BaselineNativeEngine)engine).ExecuteMacro(nativeState, instance, instance.CancellationSource.Token);
                else if (engine is LuaEngine) execute = () => ((LuaEngine)engine).ExecuteMacro(luaState, instance, instance.CancellationSource.Token);
                else execute = () => ((BaselineLuaEngine)engine).ExecuteMacro(luaState, instance, instance.CancellationSource.Token);

                // Pause races with the already queued framework-thread startup.
                var run = framework.RunOnTick(() => { entered.Set(); return execute(); });
                instance.PauseEvent.Reset();
                framework.Start();
                try
                {
                    Check(entered.Wait(2000), "Macro did not start.");
                    var heartbeat = framework.RunOnTick(() => true);
                    var responsive = heartbeat.Wait(baseline ? 200 : 2000);
                    Check(command.Calls == 0 && luaState.LuaGenerator.Calls == 0, "Macro advanced while paused.");
                    if (baseline)
                    {
                        Check(!responsive, "Baseline did not reproduce the framework deadlock.");
                        instance.CancellationSource.Cancel(); // independent watchdog, never game UI
                    }
                    else
                    {
                        Check(responsive && !run.IsCompleted, "Paused macro blocked the framework or completed early.");
                        var control = framework.RunOnTick(() => {
                            if (stop) instance.CancellationSource.Cancel();
                            else instance.PauseEvent.Set();
                            return Task.CompletedTask;
                        });
                        Check(control.Wait(2000), "Resume/Stop was blocked.");
                    }
                    Check(run.Wait(2000) && heartbeat.Wait(2000), "Macro did not finish after control action.");
                    Check(engine.Errors.Count == 0, "Engine reported an error.");
                    var calls = lua ? luaState.LuaGenerator.Calls : command.Calls;
                    Check(calls == (baseline || stop ? 0 : 1), "Unexpected execution after Resume/Stop.");
                }
                finally
                {
                    instance.CancellationSource.Cancel();
                    Check(run.Wait(2000), "Watchdog could not stop fixture.");
                }
                Console.WriteLine("PASS " + (lua ? "Lua" : "native") + ": " +
                    (baseline ? "old blocking wait freezes framework (watchdog recovered)" :
                    stop ? "Stop cancels paused startup; framework remains responsive" :
                    "Resume releases paused startup; execution stays on framework thread"));
            }
        }
        public static void RunAll()
        {
            QueuedPause(false, true, false);
            QueuedPause(true, true, false);
            QueuedPause(false, false, false);
            QueuedPause(true, false, false);
            QueuedPause(false, false, true);
            QueuedPause(true, false, true);
            using (var gate = new ManualResetEventSlim(true))
            using (var cancellation = new CancellationTokenSource())
            {
                Check(MacroPause.WaitAsync(gate, cancellation.Token).IsCompletedSuccessfully, "Running macro did not use fast path.");
                for (var i = 0; i < 3; ++i)
                {
                    gate.Reset();
                    var wait = MacroPause.WaitAsync(gate, cancellation.Token);
                    Check(!wait.IsCompleted, "Repeated pause failed to wait.");
                    gate.Set();
                    Check(wait.Wait(2000), "Repeated resume failed.");
                }
                cancellation.Cancel();
                Check(MacroPause.WaitAsync(gate, cancellation.Token).IsCanceled, "Cancellation was ignored for a signaled gate.");
                Console.WriteLine("PASS running fast path, repeated Pause/Resume, and cancellation before execution");
            }
            Console.WriteLine("7 tests passed");
        }
    }
}
