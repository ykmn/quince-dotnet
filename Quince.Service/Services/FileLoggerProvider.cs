using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly LoggerExternalScopeProvider _scopeProvider = new();
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private DateTime _lastWriteFailureReportedAt = DateTime.MinValue;

    public LogLevel MinLevel { get; private set; }

    public FileLoggerProvider(string logDir, AppConfig appConfig)
    {
        _logDir = logDir;
        MinLevel = ParseLevel(appConfig.LogLevel);
        Directory.CreateDirectory(_logDir);
        CleanupOldLogs(appConfig.LogRetentionDays);
    }

    /// <summary>Applied live from the Settings dialog — existing FileLogger instances read MinLevel dynamically.</summary>
    public void UpdateSettings(AppConfig appConfig)
    {
        MinLevel = ParseLevel(appConfig.LogLevel);
        CleanupOldLogs(appConfig.LogRetentionDays);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, _scopeProvider);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// A file-write failure here (permissions, disk full, an antivirus/EDR lock, another instance
    /// holding the file open, ...) must never take down the whole app: Microsoft.Extensions.Logging
    /// otherwise lets a provider's exception propagate straight up through every
    /// <c>ILogger.Log</c> call site, all the way to an unhandled <c>AggregateException</c> — observed
    /// live as the very first log line (a fresh <c>log/</c> folder without write permission for the
    /// running user) crashing the process before a single channel could even start, for a 24/7
    /// recorder where that's about as bad an outcome as a logging hiccup can have. Falls back to
    /// stderr instead, throttled so a persistent failure doesn't spam it on every single log call.
    /// </summary>
    internal void WriteLine(LogLevel level, string channel, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelLabel(level)}] [{channel}] {message}";
        lock (_lock)
        {
            try
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer = null; // don't reuse a possibly-broken writer — retry from scratch next call
                ReportWriteFailure(ex, line);
            }
        }
    }

    private void ReportWriteFailure(Exception ex, string line)
    {
        if (DateTime.UtcNow - _lastWriteFailureReportedAt < TimeSpan.FromSeconds(5)) return;
        _lastWriteFailureReportedAt = DateTime.UtcNow;
        Console.Error.WriteLine($"[FileLogger] Не удалось записать в файл лога ({_logDir}): {ex.Message}");
        Console.Error.WriteLine(line);
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
