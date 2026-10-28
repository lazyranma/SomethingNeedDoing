using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KeraLua;
using Lua = NLua.Lua;

namespace SomethingNeedDoing.LuaMacro;

/// <summary>
/// Diagnostic-only hooks for NLua 1.7.9. Never hash, format, or read properties of game objects.
/// Keep this instance alive until after Lua.Dispose: lua_close can run userdata finalizers too.
/// </summary>
internal sealed class LuaCrashDiagnostics
{
    // Temporary diagnostic build settings. These travel with the source, not SND metadata.
    internal static readonly bool Enabled = true;
    private static readonly FieldInfo? TranslatorField = typeof(Lua).GetField("_translator", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ObjectsField = typeof(NLua.ObjectTranslator).GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? NextObjectField = typeof(NLua.ObjectTranslator).GetField("_nextObj", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Lua _lua;
    private readonly Action<string> _write;
    private readonly object? _translator;
    private readonly Dictionary<int, object>? _objects;
    private readonly Dictionary<int, string> _seen = new();
    private readonly HashSet<Type> _gcTypes = new();
    private readonly LuaHookFunction _hook; // Root the native callback, including during lua_close.
    private readonly LuaFunction _gcHook;
    private readonly string _identity;
    private int _nextObject = -1;
    private string _location = "<before first Lua line>";
    private long _step;
    private bool _hookFailed;

    internal LuaCrashDiagnostics(Lua lua, string macroId, string macroName, Action<string> write)
    {
        _lua = lua;
        _write = write;
        _identity = $"run={Guid.NewGuid():N} macro={Clean(macroName)} id={Clean(macroId)} state=0x{lua.State.Handle:X}";
        _translator = TranslatorField?.GetValue(lua);
        _objects = _translator == null ? null : ObjectsField?.GetValue(_translator) as Dictionary<int, object>;
        _hook = OnHook;
        _gcHook = OnCollect;
        lua.State.SetHook(_hook, LuaHookMask.Line | LuaHookMask.Call | LuaHookMask.Return, 0);
        Record($"START nlua={typeof(Lua).Assembly.GetName().Version} translatorInspection={_objects != null}");
    }

    internal void Record(string phase)
    {
        try { _write($"{_identity} step={_step} thread={Environment.CurrentManagedThreadId} {phase} last={_location}"); }
        catch (Exception) { /* Diagnostic sinks must not change macro/native callback behavior. */ }
    }

    internal void Begin(string phase)
    {
        ++_step;
        Record("BEGIN " + phase);
        ObserveSafely();
    }

    internal void End(string phase)
    {
        ObserveSafely();
        Record("END " + phase);
    }

    private void OnHook(IntPtr statePointer, IntPtr debugPointer)
    {
        // Use the callback's actual state: coroutine.wrap runs on a child Lua thread.
        // Never throw across this reverse P/Invoke boundary.
        try
        {
            var state = KeraLua.Lua.FromIntPtr(statePointer);
            if (!state.GetInfo("nSl", debugPointer)) return;
            var info = LuaDebug.FromIntPtr(debugPointer);
            ObserveSafely(state); // Objects created by the previous line/C call get that location.
            if (info.Event == LuaHookEvent.Line)
            {
                var source = info.Source;
                var label = source != null && (source.StartsWith('@') || source.StartsWith('='))
                    ? source[1..] : "<string chunk>";
                _location = $"{Clean(label)}:{info.CurrentLine} function={Clean(info.Name)}";
            }
        }
        catch (Exception ex)
        {
            if (_hookFailed) return;
            _hookFailed = true;
            Record($"HOOK-ERROR {ex.GetType().Name}: {Clean(ex.Message)}");
        }
    }

    private void ObserveSafely(KeraLua.Lua? state = null)
    {
        if (_hookFailed) return;
        try { ObserveObjects(state); }
        catch (Exception ex)
        {
            _hookFailed = true;
            Record($"INSPECTION-ERROR {ex.GetType().Name}: {Clean(ex.Message)}");
        }
    }

    private void ObserveObjects(KeraLua.Lua? state = null)
    {
        if (_objects == null || _translator == null) return;
        var next = NextObjectField?.GetValue(_translator) as int?;
        if (next.HasValue && next.Value == _nextObject) return;
        _nextObject = next ?? -1;
        foreach (var pair in _objects)
        {
            var typeName = pair.Value.GetType().FullName ?? "";
            if (!typeName.StartsWith("Dalamud.Game.ClientState.Objects.", StringComparison.Ordinal) &&
                typeName != "SomethingNeedDoing.LuaMacro.Wrappers.EntityWrapper") continue;
            WrapCollector(state ?? _lua.State, pair.Value.GetType());
            if (_seen.ContainsKey(pair.Key)) continue;
            var description = Describe(pair.Key, pair.Value);
            _seen[pair.Key] = _location;
            Record("OBJECT-FIRST-SEEN " + description);
        }
        // Only primitive IDs and strings are retained; never extend a game object's lifetime.
        foreach (var id in new List<int>(_seen.Keys))
            if (!_objects.ContainsKey(id)) _seen.Remove(id);
    }

    private void WrapCollector(KeraLua.Lua state, Type type)
    {
        if (_gcTypes.Contains(type)) return;
        var top = state.GetTop();
        try
        {
            state.GetMetaTable(type.AssemblyQualifiedName);
            if (state.Type(-1) != LuaType.Table) return;
            state.PushString("__gc");
            state.RawGet(-2);
            var original = state.ToCFunction(-1);
            if (original == null || Marshal.GetFunctionPointerForDelegate(original) !=
                Marshal.GetFunctionPointerForDelegate(NLua.MetaFunctions.GcFunction)) return;
            state.Pop(1);
            state.PushString("__gc");
            state.PushCFunction(_gcHook);
            state.RawSet(-3);
            _gcTypes.Add(type);
            Record($"GC-WRAPPED type={Clean(type.FullName)}");
        }
        finally { state.SetTop(top); }
    }

    private int OnCollect(IntPtr statePointer)
    {
        // Lua disables debug hooks during __gc, so wrap only the known NLua collector
        // on observed game-object metatables, forwarding its original argument unchanged.
        var description = "unknown userdata";
        try
        {
            var state = KeraLua.Lua.FromIntPtr(statePointer);
            if (state.Type(1) == LuaType.UserData && state.RawLen(1) == sizeof(int))
            {
                var id = Marshal.ReadInt32(state.ToUserData(1)); // NLua-owned ID; never game memory.
                description = _objects != null && _objects.TryGetValue(id, out var obj)
                    ? Describe(id, obj) : $"object={id} already-removed";
                if (_seen.TryGetValue(id, out var firstSeen)) description += $" firstSeen={firstSeen}";
            }
            Record("GC-Call " + description);
        }
        catch (Exception) { /* A diagnostic failure must not prevent the original collector. */ }
        var result = NLua.MetaFunctions.GcFunction(statePointer);
        try { Record("GC-Return " + description); }
        catch (Exception) { /* Never throw a logging error across the native boundary. */ }
        return result;
    }

    private static string Describe(int id, object obj)
        => $"object={id} type={Clean(obj.GetType().FullName)} managedIdentity={RuntimeHelpers.GetHashCode(obj):X}";

    private static string Clean(string? text) => (text ?? "?").Replace('\r', ' ').Replace('\n', ' ');
}
