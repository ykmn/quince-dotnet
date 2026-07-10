using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class AudioEngineManager : IHostedService
{
    private readonly ChannelManager _channelManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AudioEngineManager> _logger;
    private readonly AppSettingsService _appSettings;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public string FfmpegPath => _ffmpegPath;
    public double PlayoutBufferSeconds => _appSettings.Current.PlayoutBufferSeconds;

    private readonly Dictionary<string, ChannelEngine> _engines = new();
    private readonly object _lock = new();

    public event Action<string, LevelReading>? LevelUpdated;
    public event Action<string, EngineStatus>? StatusUpdated;
    public event Action<string, GoniometerFrame>? GoniometerUpdated;
    public event Action<string, string>? MetadataUpdated;

    public AudioEngineManager(ChannelManager channelManager, ILoggerFactory loggerFactory,
        ILogger<AudioEngineManager> logger, IConfiguration configuration, AppSettingsService appSettings)
    {
        _channelManager = channelManager;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _appSettings = appSettings;
        _ffmpegPath = PathResolver.Resolve(configuration["FfmpegPath"], "tools/ffmpeg.exe");
        _ffprobePath = PathResolver.Resolve(configuration["FfprobePath"], "tools/ffprobe.exe");

        _channelManager.ChannelAdded += OnChannelAdded;
        _channelManager.ChannelUpdated += OnChannelUpdated;
        _channelManager.ChannelRemoved += OnChannelRemoved;
    }

    private static bool IsEligible(Configuration.ChannelConfig config) => config.Source.Type is "stream" or "soundcard";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Runs after ChannelManager.StartAsync (registered later in Program.cs — the
        // generic host awaits hosted services' StartAsync in registration order), so
        // _channelManager.Channels is already populated here.
        foreach (var config in _channelManager.Channels)
        {
            if (IsEligible(config) && config.AutoStart)
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

    public string? GetMetadataText(string channelName)
    {
        lock (_lock)
        {
            return _engines.TryGetValue(channelName, out var engine) ? engine.MetadataText : null;
        }
    }

    /// <summary>True only while the channel is actually capturing — not merely "has an entry in
    /// _engines", which also covers a channel that gave up after exhausting its reconnect-attempt
    /// budget (EngineStatus.HasError) and is sitting there dead until someone starts it again.</summary>
    public bool IsRunning(string channelName)
    {
        lock (_lock) { return _engines.TryGetValue(channelName, out var engine) && engine.Status.IsRecording; }
    }

    /// <summary>For output-monitoring playback: subscribes an extra consumer to a running channel's
    /// raw audio. Returns null if the channel isn't currently running.</summary>
    public System.Threading.Channels.ChannelReader<AudioChunk>? SubscribeAudio(string channelName, string consumerId)
    {
        lock (_lock) { return _engines.TryGetValue(channelName, out var engine) ? engine.Subscribe(consumerId) : null; }
    }

    public void UnsubscribeAudio(string channelName, string consumerId)
    {
        lock (_lock) { if (_engines.TryGetValue(channelName, out var engine)) engine.Unsubscribe(consumerId); }
    }

    /// <returns>(started, eligible) — eligible counts channels whose source type supports recording, whether or not they were already running.</returns>
    public (int Started, int Eligible) StartAll()
    {
        var started = 0;
        var eligible = 0;
        foreach (var config in _channelManager.Channels)
        {
            if (!IsEligible(config)) continue;
            eligible++;
            if (IsRunning(config.Name)) continue;
            TryStart(config.Name);
            if (IsRunning(config.Name)) started++;
        }
        return (started, eligible);
    }

    public int StopAll()
    {
        var stopped = 0;
        foreach (var config in _channelManager.Channels)
        {
            if (!IsRunning(config.Name)) continue;
            Stop(config.Name);
            stopped++;
        }
        return stopped;
    }

    /// <param name="suppressRecording">Passed straight through to <see cref="ChannelEngine.Start"/> —
    /// see its doc comment. Used by <see cref="AudioPlaybackService"/>'s auto-start-for-listen-in so
    /// that path never silently begins writing a real recording file.</param>
    public void Start(string channelName, bool suppressRecording = false)
    {
        lock (_lock)
        {
            if (_engines.TryGetValue(channelName, out var existingEngine))
            {
                if (existingEngine.Status.IsRecording) return; // already running — no-op, as before

                // Stopped itself after exhausting its reconnect-attempt budget (EngineStatus.HasError) —
                // OnReconnectExhausted only calls the ChannelEngine's own Stop(), it never goes through
                // this class's Stop() below, so the entry is still sitting in _engines pointing at a dead
                // engine. Without this, a manual "Старт" on a channel in that state would hit the
                // ContainsKey check above and silently do nothing forever, even once the source is
                // reachable again. Drop the stale entry so a fresh ChannelEngine gets created below.
                _engines.Remove(channelName);
            }

            var config = _channelManager.Channels.FirstOrDefault(c => c.Name == channelName);
            if (config == null || !IsEligible(config)) return;

            var engine = new ChannelEngine(config, _ffmpegPath, _ffprobePath, _loggerFactory,
                reading => PushLevel(channelName, reading),
                status => PushStatus(channelName, status),
                frame => PushGoniometer(channelName, frame),
                () => _appSettings.Current.AdKeywords,
                () => _appSettings.Current.ReconnectDelaySeconds,
                () => _appSettings.Current.ReconnectMaxAttempts,
                () => _appSettings.Current.NewsKeywords,
                text => PushMetadata(channelName, text),
                () => _appSettings.Current.PlayoutBufferSeconds);

            _engines[channelName] = engine;
            try
            {
                engine.Start(suppressRecording);
            }
            catch (Exception ex)
            {
                _engines.Remove(channelName);
                using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName }))
                    _logger.LogError(ex, "Не удалось запустить канал");
                throw;
            }
        }
    }

    private void TryStart(string channelName)
    {
        try { Start(channelName); }
        catch (Exception) { /* already logged in Start() */ }
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

    private void OnChannelAdded(Configuration.ChannelConfig config)
    {
        if (IsEligible(config) && config.AutoStart)
            TryStart(config.Name);
    }

    private void OnChannelUpdated(Configuration.ChannelConfig oldConfig, Configuration.ChannelConfig newConfig)
    {
        lock (_lock)
        {
            if (!_engines.TryGetValue(oldConfig.Name, out var engine)) return;
            _engines.Remove(oldConfig.Name);
            if (!IsEligible(newConfig))
            {
                engine.Stop();
                return;
            }
            engine.UpdateConfig(newConfig);
            _engines[newConfig.Name] = engine;
        }
    }

    private void OnChannelRemoved(Configuration.ChannelConfig config)
    {
        Stop(config.Name);
    }

    private void PushLevel(string channelName, LevelReading reading)
    {
        try
        {
            LevelUpdated?.Invoke(channelName, reading);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в обработчике LevelUpdated");
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
            _logger.LogError(ex, "Ошибка в обработчике StatusUpdated");
        }
    }

    private void PushGoniometer(string channelName, GoniometerFrame frame)
    {
        try
        {
            GoniometerUpdated?.Invoke(channelName, frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в обработчике GoniometerUpdated");
        }
    }

    private void PushMetadata(string channelName, string text)
    {
        try
        {
            MetadataUpdated?.Invoke(channelName, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в обработчике MetadataUpdated");
        }
    }
}
