using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class AudioEngineManager : IHostedService
{
    private readonly ChannelManager _channelManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AudioEngineManager> _logger;
    private readonly AppSettingsService _appSettings;
    private readonly Audio.HlsSegmentDurationService _hlsSegmentDurationService;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public string FfmpegPath => _ffmpegPath;

    /// <summary>The non-HLS (Icecast/soundcard/Livewire) playout-buffer delay, and the last-resort
    /// fallback for a channel not found in <see cref="_engines"/> at read time — normal per-channel
    /// reads should prefer <see cref="GetPlayoutBufferSeconds"/>, which reflects each running
    /// channel's own source-aware resolved delay (see <see cref="ChannelEngine.PlayoutBufferSeconds"/>).</summary>
    public double PlayoutBufferSeconds => _appSettings.Current.PlayoutBufferSeconds;

    private readonly Dictionary<string, ChannelEngine> _engines = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _startSweepCts;
    private Task? _startSweepTask;

    public event Action<string, LevelReading>? LevelUpdated;
    public event Action<string, EngineStatus>? StatusUpdated;
    public event Action<string, GoniometerFrame>? GoniometerUpdated;
    public event Action<string, string>? MetadataUpdated;

    public AudioEngineManager(ChannelManager channelManager, ILoggerFactory loggerFactory,
        ILogger<AudioEngineManager> logger, IConfiguration configuration, AppSettingsService appSettings,
        Audio.HlsSegmentDurationService hlsSegmentDurationService)
    {
        _channelManager = channelManager;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _appSettings = appSettings;
        _hlsSegmentDurationService = hlsSegmentDurationService;
        _ffmpegPath = PathResolver.Resolve(configuration["FfmpegPath"], "tools/ffmpeg.exe");
        _ffprobePath = PathResolver.Resolve(configuration["FfprobePath"], "tools/ffprobe.exe");

        _channelManager.ChannelAdded += OnChannelAdded;
        _channelManager.ChannelUpdated += OnChannelUpdated;
        _channelManager.ChannelRemoved += OnChannelRemoved;
    }

    private static bool IsEligible(Configuration.ChannelConfig config) => config.Source.Type is "stream" or "soundcard" or "livewire";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Runs after ChannelManager.StartAsync (registered later in Program.cs — the
        // generic host awaits hosted services' StartAsync in registration order), so
        // _channelManager.Channels is already populated here.
        //
        // The generic host doesn't start Kestrel listening until every IHostedService.StartAsync
        // (this one included) returns — starting every AutoStart channel here synchronously used to
        // make the web interface unreachable for as long as it took every single channel to finish
        // starting (device connects, retention cleanup, ...), sequentially, one after another,
        // because AudioEngineManager.Start() holds one shared lock for its whole duration. Kicking
        // the whole sweep off from a background task instead lets Kestrel start listening
        // immediately; channels then show up as started one by one via the normal StatusUpdated
        // event as each finishes, instead of the whole UI being blank/unreachable until the last one
        // is done. Deliberately not passed `cancellationToken` (the host's startup-timeout token,
        // which is only valid for the duration of this call) — the sweep must keep running to
        // completion after StartAsync itself has already returned.
        _startSweepCts = new CancellationTokenSource();
        var sweepCt = _startSweepCts.Token;
        _startSweepTask = Task.Run(() =>
        {
            try
            {
                foreach (var config in _channelManager.Channels)
                {
                    if (sweepCt.IsCancellationRequested) break;
                    if (IsEligible(config) && config.AutoStart)
                        Start(config.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка автозапуска каналов");
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The auto-start sweep above now runs in the background, so it could still be in the
        // middle of adding an engine to _engines when a shutdown comes in right after startup.
        // Signal it to stop starting any further channels and wait for whatever Start() call it's
        // currently inside of to finish, before clearing/stopping everything below — otherwise a
        // channel started concurrently with this method could get added to _engines just after the
        // clear below, and never get stopped (a leaked running capture/ffmpeg process).
        _startSweepCts?.Cancel();
        try { _startSweepTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }

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

    /// <summary>The running channel's actual capture sample rate (see <see cref="ChannelEngine.SampleRate"/>),
    /// or null if the channel isn't currently running.</summary>
    public int? GetSampleRate(string channelName)
    {
        lock (_lock) { return _engines.TryGetValue(channelName, out var engine) ? engine.SampleRate : null; }
    }

    /// <summary>The playout-buffer delay actually resolved for the channel's current run (see
    /// <see cref="ChannelEngine.PlayoutBufferSeconds"/>) — source-aware (small for Icecast/soundcard/
    /// Livewire, measured-segment-based for HLS). Null if the channel isn't currently running.</summary>
    public double? GetPlayoutBufferSeconds(string channelName)
    {
        lock (_lock) { return _engines.TryGetValue(channelName, out var engine) ? engine.PlayoutBufferSeconds : null; }
    }

    /// <summary>Every currently-running channel's subprocess PIDs (see <see cref="ChannelEngine.GetProcessIds"/>),
    /// for the admin "Монитор ресурсов" dialog (<see cref="ProcessMonitorService"/>).</summary>
    public IReadOnlyList<(string ChannelName, string Role, int Pid)> GetTrackedProcesses()
    {
        lock (_lock)
        {
            var result = new List<(string, string, int)>();
            foreach (var (channelName, engine) in _engines)
                foreach (var (role, pid) in engine.GetProcessIds())
                    result.Add((channelName, role, pid));
            return result;
        }
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
                () => _appSettings.Current.PlayoutBufferSeconds,
                () => _appSettings.Current.LivewireNic,
                _hlsSegmentDurationService);

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
