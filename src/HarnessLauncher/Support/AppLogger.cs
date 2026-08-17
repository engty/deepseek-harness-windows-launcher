using System.Text;
using System.Collections.Concurrent;

namespace HarnessLauncher.Support;

/// <summary>
/// Minimal file+debug logger mirroring the macOS AppLogger (OSLog) behaviour:
/// categorized, leveled, and always redacted before persisting.
/// </summary>
public static class AppLogger
{
    public enum Level { Debug, Info, Error }

    private static readonly ConcurrentQueue<string> _pending = new();
    private static readonly object _writeLock = new();
    private static string? _logFile;
    private static int _dropped;

    public static void Configure(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFile = Path.Combine(logDirectory, "launcher.log");
        }
        catch
        {
            _logFile = null;
        }
    }

    public static void Log(Level level, string category, string message)
    {
        var redacted = SensitiveDataRedactor.Redact(message);
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {redacted}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (_writeLock)
        {
            _pending.Enqueue(line);
            Drain();
        }
    }

    private static void Drain()
    {
        if (_logFile is null) return;
        try
        {
            RotateIfNeeded();
            var sb = new StringBuilder();
            while (_pending.TryDequeue(out var line))
            {
                if (_dropped > 0)
                {
                    sb.AppendLine($"[logger] dropped {_dropped} lines");
                    _dropped = 0;
                }
                sb.AppendLine(line);
            }
            if (sb.Length > 0)
            {
                File.AppendAllText(_logFile, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            _dropped++;
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (_logFile is null || !File.Exists(_logFile)) return;
            if (new FileInfo(_logFile).Length < 2_000_000) return;
            var rotated = Path.Combine(
                Path.GetDirectoryName(_logFile)!,
                $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(_logFile, rotated, overwrite: true);
            var directory = Path.GetDirectoryName(_logFile)!;
            var old = Directory.GetFiles(directory, "launcher-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(5);
            foreach (var file in old)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
