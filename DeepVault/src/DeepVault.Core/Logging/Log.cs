using System.Text.Json;

namespace DeepVault.Core.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

/// <summary>
/// Structured JSON-lines log. Callers must not pass file contents; messages describe operations,
/// counts and offsets only. Files rotate at 10 MB and only the last 5 are kept.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly string _dir;
    private readonly object _lock = new();
    private StreamWriter? _w;
    private long _bytes;
    private const long MaxBytes = 10 * 1024 * 1024;

    public string Directory => _dir;
    public string CurrentFile { get; private set; } = "";
    public event Action<string, string>? Written;

    public FileLog(string directory)
    {
        _dir = directory;
        System.IO.Directory.CreateDirectory(directory);
        Open();
    }

    private void Open()
    {
        CurrentFile = Path.Combine(_dir, $"deepvault-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _w = new StreamWriter(new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _bytes = 0;
        foreach (var old in new DirectoryInfo(_dir).GetFiles("deepvault-*.log").OrderByDescending(f => f.Name).Skip(5))
            try { old.Delete(); } catch (IOException) { }
    }

    private void Write(string level, string msg, Exception? ex)
    {
        lock (_lock)
        {
            var line = JsonSerializer.Serialize(new { t = DateTime.UtcNow.ToString("O"), level, msg, error = ex?.GetType().Name, detail = ex?.Message });
            try
            {
                _w!.WriteLine(line);
                _bytes += line.Length + 1;
                if (_bytes > MaxBytes) { _w.Dispose(); Open(); }
            }
            catch (IOException) { /* logging must never break recovery */ }
        }
        Written?.Invoke(level, msg);
    }

    public void Info(string message) => Write("info", message, null);
    public void Warn(string message) => Write("warn", message, null);
    public void Error(string message, Exception? ex = null) => Write("error", message, ex);
    public void Dispose() { lock (_lock) _w?.Dispose(); }
}
