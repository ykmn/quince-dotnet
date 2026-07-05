using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class AudioEngineManager : IHostedService
{
    private readonly ChannelManager _channelManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AudioEngineManager> _logger;
    private readonly string _ffmpegPath;

    private readonly Dictionary<string, ChannelEngine> _engines = new();
    private readonly object _lock = new();

    public event Action<string, LevelReading>? LevelUpdated;
    public event Action<string, EngineStatus>? StatusUpdated;

    public AudioEngineManager(ChannelManager channelManager, ILoggerFactory loggerFactory,
        ILogger<AudioEngineManager> logger, IConfiguration configuration)
    {
        _channelManager = channelManager;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _ffmpegPath = PathResolver.Resolve(configuration["FfmpegPath"], "tools/ffmpeg.exe");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Runs after ChannelManager.StartAsync (registered later in Program.cs — the
        // generic host awaits hosted services' StartAsync in registration order), so
        // _channelManager.Channels is already populated here.
        foreach (var config in _channelManager.Channels)
        {
            if (config.Source.Type == "stream" && config.AutoStart)
                Start(config.Name);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var engine in _engines.Values) engine.Stop();
            _engines.Clear();
        }
        return Task.CompletedTask;
    }

    public EngineStatus? GetStatus(string channelName)
    {
        lock (_lock)
        {
            return _engines.TryGetValue(channelName, out var engine) ? engine.Status : null;
        }
    }

    public void Start(string channelName)
    {
        lock (_lock)
        {
            if (_engines.ContainsKey(channelName)) return;

            var config = _channelManager.Channels.FirstOrDefault(c => c.Name == channelName);
            if (config == null || config.Source.Type != "stream") return;

            var engine = new ChannelEngine(config, _ffmpegPath, _loggerFactory,
                reading => PushLevel(channelName, reading),
                status => PushStatus(channelName, status));

            _engines[channelName] = engine;
            try
            {
                engine.Start();
            }
            catch (Exception ex)
            {
                _engines.Remove(channelName);
                _logger.LogError(ex, "Не удалось запустить канал '{Channel}'", channelName);
                throw;
            }
        }
    }

    public void Stop(string channelName)
    {
        ChannelEngine? engine;
        lock (_lock)
        {
            if (!_engines.TryGetValue(channelName, out engine)) return;
            _engines.Remove(channelName);
        }
        engine.Stop();
    }

    private void PushLevel(string channelName, LevelReading reading)
    {
        try
        {
            LevelUpdated?.Invoke(channelName, reading);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в обработчике LevelUpdated для канала '{Channel}'", channelName);
        }
    }

    private void PushStatus(string channelName, EngineStatus status)
    {
        try
        {
            StatusUpdated?.Invoke(channelName, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в обработчике StatusUpdated для канала '{Channel}'", channelName);
        }
    }
}
