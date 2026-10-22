using System.Globalization;
using System.Text;

namespace Waikan.Services;

public sealed class LogService
{
    private const long MaxLogBytes = 10 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly string _logDirectory;
    private string _currentDate = string.Empty;
    private string _currentPath = string.Empty;

    public LogService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
        CleanupOldLogs();
        EnsureFile();
    }

    public string LogDirectory => _logDirectory;

    public string CurrentLogPath
    {
        get
        {
            lock (_sync)
            {
                EnsureFile();
                return _currentPath;
            }
        }
    }

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);

    public void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Write("ERR", message);
            return;
        }

        Write("ERR", $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_sync)
            {
                EnsureFile();
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_currentPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void EnsureFile()
    {
        var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (today == _currentDate && File.Exists(_currentPath))
        {
            var info = new FileInfo(_currentPath);
            if (info.Length < MaxLogBytes)
            {
                return;
            }

            var rolled = Path.Combine(_logDirectory, $"app-{today}-{DateTime.Now:HHmmss}.log");
            try
            {
                File.Move(_currentPath, rolled, true);
            }
            catch
            {
                // If rotation fails, append to the existing file.
                return;
            }
        }

        _currentDate = today;
        _currentPath = Path.Combine(_logDirectory, $"app-{today}.log");
    }

    private void CleanupOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "app-*.log"))
            {
                var info = new FileInfo(file);
                if (info.CreationTimeUtc < DateTime.UtcNow.AddDays(-14))
                {
                    file.DeleteSafely();
                }
            }
        }
        catch
        {
            // Housekeeping is best effort.
        }
    }
}

internal static class FileSystemExtensions
{
    public static void DeleteSafely(this string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }
}
