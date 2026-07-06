using Microsoft.Extensions.Logging;

namespace Quince.Service.Services;

public class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly LoggerExternalScopeProvider _scopeProvider;

    public FileLogger(FileLoggerProvider provider, LoggerExternalScopeProvider scopeProvider)
    {
        _provider = provider;
        _scopeProvider = scopeProvider;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _scopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _provider.MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (exception != null)
            message += Environment.NewLine + exception;

        var channel = FindChannel(state) ?? FindChannelInScopes();
        _provider.WriteLine(logLevel, channel ?? "-", message);
    }

    private static string? FindChannel<TState>(TState state)
    {
        if (state is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            foreach (var kv in pairs)
            {
                if (kv.Key == "Channel")
                    return kv.Value?.ToString();
            }
        }
        return null;
    }

    private string? FindChannelInScopes()
    {
        string? found = null;
        _scopeProvider.ForEachScope<object?>((scope, _) =>
        {
            if (found != null) return;
            if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var kv in pairs)
                {
                    if (kv.Key == "Channel")
                    {
                        found = kv.Value?.ToString();
                        break;
                    }
                }
            }
        }, null);
        return found;
    }
}
