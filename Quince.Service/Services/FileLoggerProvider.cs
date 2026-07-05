using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly LogLevel _minLevel;
    private readonly LoggerExternalScopeProvider _scopeProvider = new();
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _currentDate;

    public FileLoggerProvider(string logDir, AppConfig appConfig)
    {
        _logDir = logDir;
        _minLevel = ParseLevel(appConfig.LogLevel);
        Directory.CreateDirectory(_logDir);
        CleanupOldLogs(appConfig.LogRetentionDays);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, _scopeProvider, _minLevel);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void WriteLine(LogLevel level, string channel, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelLabel(level)}] [{channel}] {message}";
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer == null || today != _currentDate)
            {
                _writer?.Dispose();
                _currentDate = today;
                var path = Path.Combine(_logDir, $"{today:yyyy-MM-dd}.log");
                _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            }
            _writer.WriteLine(line);
        }
    }

    private void CleanupOldLogs(int retentionDays)
    {
        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(_logDir, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var fileDate)
                && fileDate.ToDateTime(TimeOnly.MinValue) < cutoff)
            {
                try
                {
                    File.Delete(file);
                    WriteLine(LogLevel.Information, "-", $"Удалён устаревший файл лога: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FileLogger] Failed to delete old log {file}: {ex.Message}");
                }
            }
        }
    }

    public static LogLevel ParseLevel(string level) => level.Trim().ToUpperInvariant() switch
    {
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Information,
        "WARNING" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error or LogLevel.Critical => "ERROR",
        _ => level.ToString().ToUpperInvariant(),
    };
}
