using SomethingNeedDoing.LuaMacro;

try
{
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    ++passed;
}

// Real NLua/KeraLua, real native Lua GC, no game or Dalamud attachment.
var messages = new List<string>();
var lua = new NLua.Lua();
var diagnostics = new LuaCrashDiagnostics(lua, "test-id", "Test Macro", messages.Add);
var obj = new Dalamud.Game.ClientState.Objects.Types.DiagnosticGameObject();
lua["probe"] = obj;
var hashesBeforeHook = obj.HashCalls;
diagnostics.Begin("resume");
lua.DoString("local a = 1\na = a + 1", "@lines.lua");
diagnostics.End("resume");
Check(obj.HashCalls == hashesBeforeHook, "diagnostics never call the game object's GetHashCode");
Check(messages.Any(s => s.Contains("OBJECT-FIRST-SEEN") && s.Contains("DiagnosticGameObject")), "managed object type and translator ID recorded");
Check(messages.Any(s => s.Contains("last=lines.lua:2")), "last executed Lua source and line recorded");
obj.OnHash = () => messages.Add("ORIGINAL-HASH");
lua.DoString("probe = nil; collectgarbage('collect')", "@gc.lua");
Check(messages.Any(s => s.Contains("GC-Call") && s.Contains("DiagnosticGameObject")), "exact CLR object recorded before native NLua GC");
Check(messages.Any(s => s.Contains("GC-Return")), "native GC completion recorded");
Check(messages.FindIndex(s => s.Contains("GC-Call") && s.Contains("DiagnosticGameObject")) < messages.IndexOf("ORIGINAL-HASH"), "GC breadcrumb precedes original object's hash lookup");

messages.Clear();
using var coroutine = (NLua.LuaFunction)lua.DoString("return coroutine.wrap(function()\n local x = 1\n coroutine.yield('wait')\n x = x + 1\nend)", "@coroutine.lua")[0];
diagnostics.Begin("resume");
var result = coroutine.Call();
diagnostics.End("resume");
Check(result.Length == 1 && (string)result[0] == "wait", "hook preserves coroutine yields");
Check(messages.Any(s => s.Contains("last=coroutine.lua:3")), "line hook follows child coroutine state");
coroutine.Call();

// A new raw game object created inside Lua must be seen at the acquisition line.
lua["factory"] = new Func<object>(() => new Dalamud.Game.ClientState.Objects.Types.DiagnosticGameObject());
lua.DoString("local held = factory()\nlocal n = 2\nheld = nil\ncollectgarbage('collect')", "@acquire.lua");
Check(messages.Any(s => s.Contains("OBJECT-FIRST-SEEN") && s.Contains("last=acquire.lua:1")), "object acquisition correlated with producing Lua line");
Check(messages.Any(s => s.Contains("GC-Call") && s.Contains("firstSeen=acquire.lua:1")), "GC retains first observation source and line");
lua["subkind"] = new Dalamud.Game.ClientState.Objects.SubKinds.DiagnosticBattleNpc();
diagnostics.Begin("subkind");
lua.DoString("subkind = nil; collectgarbage('collect')");
Check(messages.Any(s => s.Contains("GC-Call") && s.Contains("DiagnosticBattleNpc")), "Dalamud SubKinds get GC tracing as well as base object types");

// Keep the callback rooted while lua_close runs pending finalizers.
lua["disposeProbe"] = new Dalamud.Game.ClientState.Objects.Types.DiagnosticGameObject();
diagnostics.Begin("dispose");
lua.Dispose();
diagnostics.Record("END dispose");
GC.KeepAlive(diagnostics);
Check(messages.Any(s => s.Contains("GC-Call") && s.Contains("DiagnosticGameObject")) && messages.Last().Contains("END dispose"), "lua_close finalizers leave diagnostics alive");

var sinkFailureLua = new NLua.Lua();
var sinkFailureDiagnostics = new LuaCrashDiagnostics(sinkFailureLua, "other-id", "Other Macro", _ => throw new IOException("simulated disk failure"));
sinkFailureLua["probe"] = new Dalamud.Game.ClientState.Objects.Types.DiagnosticGameObject();
sinkFailureDiagnostics.Begin("resume");
sinkFailureLua.DoString("probe = nil; collectgarbage('collect')");
sinkFailureLua.Dispose();
GC.KeepAlive(sinkFailureDiagnostics);
Check(true, "log failures do not interrupt macro execution or userdata GC");

var directory = Path.Combine(Path.GetTempPath(), "snd-diagnostics-" + Guid.NewGuid().ToString("N"));
using (var log = new LuaCrashLog(directory))
{
    log.Write("BEGIN durable");
    using (var reader = new StreamReader(new FileStream(Path.Combine(directory, "lua-crash.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        Check(reader.ReadToEnd().Contains("BEGIN durable"), "breadcrumb readable before writer is disposed");
    for (var i = 0; i < 28; ++i) log.Write(new string('x', 1024 * 1024));
    Check(File.Exists(Path.Combine(directory, "lua-crash.log.2")) && Directory.GetFiles(directory).Length == 3, "rotation bounds journal to three files");
}
// Only delete known test files in the unique test directory.
foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
Directory.Delete(directory);
Console.WriteLine($"{passed} checks passed");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

namespace Dalamud.Game.ClientState.Objects.Types
{
    public sealed class DiagnosticGameObject
    {
        public int HashCalls;
        public Action? OnHash;
        public override int GetHashCode() { ++HashCalls; OnHash?.Invoke(); return 42; }
        public override string ToString() => throw new Exception("Diagnostic must not format game objects");
    }
}

namespace Dalamud.Game.ClientState.Objects.SubKinds
{
    public sealed class DiagnosticBattleNpc
    {
        public override string ToString() => throw new Exception("Do not read native properties");
    }
}

namespace SomethingNeedDoing.LuaMacro
{
    internal static class FrameworkLogger
    {
        internal static void Warning(string message) => Console.WriteLine(message);
    }
}
