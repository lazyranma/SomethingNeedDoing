using System.IO;
using System.Text;

namespace SomethingNeedDoing.LuaMacro;

/// <summary>Bounded synchronous breadcrumbs, independent of Dalamud's log level/async buffering.</summary>
internal sealed class LuaCrashLog : IDisposable
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private StreamWriter? _writer;
    private bool _failed;
    private bool _disposed;

    internal LuaCrashLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "lua-crash.log");
    }

    internal void Write(string message)
    {
        lock (_gate)
        {
            if (_failed || _disposed) return;
            try
            {
                _writer ??= Open();
                if (_writer.BaseStream.Length >= MaxBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    if (File.Exists(_path + ".1")) File.Move(_path + ".1", _path + ".2", true);
                    File.Move(_path, _path + ".1", true);
                    _writer = Open();
                }
                _writer.WriteLine($"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}");
                _writer.Flush(); // Preserve the last BEGIN / GC-Call on a native process crash.
            }
            catch (Exception ex)
            {
                _failed = true;
                FrameworkLogger.Warning($"Lua crash diagnostics disabled after a log write failure: {ex.Message}");
            }
        }
    }

    private StreamWriter Open() => new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
