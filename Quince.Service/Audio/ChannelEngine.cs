using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class ChannelEngine
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<LevelReading> _onLevelUpdate;
    private readonly Action<EngineStatus> _onStatusChange;
    private readonly Action<GoniometerFrame> _onGoniometerUpdate;
    private readonly Action<string>? _onMetadataUpdate;
    private readonly Func<IReadOnlyList<string>>? _getAdKeywords;
    private readonly Func<IReadOnlyList<string>>? _getNewsKeywords;
    private readonly Func<int> _getReconnectDelaySeconds;
    private readonly Func<int> _getReconnectMaxAttempts;
    private readonly Func<double> _getPlayoutBufferSeconds;

    private volatile string? _metadataText;

    private ChannelConfig _config;
    private IAudioCapture? _capture;
    private AudioWriter? _writer;
    private PlayoutBuffer? _meterBuffer;
    private LevelMeter? _meter;
    private SilenceDetector? _silence;
    private IMetadataReader? _metadataReader;
    private MetadataWriter? _metadataWriter;
    private DateTimeOffset? _metadataStartedAt;
    private const int MetadataGraceSeconds = 30;
    private EngineStatus _status = new();
    private bool _started;
    private readonly object _statusLock = new();

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public ChannelEngine(ChannelConfig config, string ffmpegPath, string ffprobePath, ILoggerFactory loggerFactory,
        Action<LevelReading> onLevelUpdate, Action<EngineStatus> onStatusChange, Action<GoniometerFrame> onGoniometerUpdate,
        Func<IReadOnlyList<string>>? getAdKeywords = null,
        Func<int>? getReconnectDelaySeconds = null, Func<int>? getReconnectMaxAttempts = null,
        Func<IReadOnlyList<string>>? getNewsKeywords = null,
        Action<string>? onMetadataUpdate = null,
        Func<double>? getPlayoutBufferSeconds = null)
    {
        _config = config;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _loggerFactory = loggerFactory;
        _onLevelUpdate = onLevelUpdate;
        _onStatusChange = onStatusChange;
        _onGoniometerUpdate = onGoniometerUpdate;
        _getAdKeywords = getAdKeywords;
        _getReconnectDelaySeconds = getReconnectDelaySeconds ?? (() => 3);
        _getReconnectMaxAttempts = getReconnectMaxAttempts ?? (() => 0);
        _getNewsKeywords = getNewsKeywords;
        _onMetadataUpdate = onMetadataUpdate;
        _getPlayoutBufferSeconds = getPlayoutBufferSeconds ?? (() => PlayoutBuffer.DefaultTargetDelaySeconds);
    }

    public EngineStatus Status
    {
        get { lock (_statusLock) { return _status; } }
    }
    public ChannelConfig Config => _config;

    /// <summary>Last "Artist - Title" (or raw string) detected by the metadata reader, or null if
    /// none has been detected yet (or metadata isn't configured for this channel).</summary>
    public string? MetadataText => _metadataText;

    /// <summary>Exposes the running channel's raw audio to an extra consumer (e.g. output monitoring
    /// playback) alongside the meter/writer/silence-detector consumers already subscribed internally.
    /// Null if the channel isn't currently running.</summary>
    public System.Threading.Channels.ChannelReader<AudioChunk>? Subscribe(string consumerId) => _capture?.Subscribe(consumerId);

    public void Unsubscribe(string consumerId) => _capture?.Unsubscribe(consumerId);

    /// <param name="suppressRecording">When true, skip creating the <see cref="AudioWriter"/> even
    /// if <see cref="ChannelConfig.RecordAudio"/> is set — used by
    /// <see cref="Services.AudioPlaybackService"/>'s temporary auto-start for browser listen-in on a
    /// stopped channel, which should capture just enough to stream audio without silently starting a
    /// real disk recording as a side effect of clicking ▶ (docs/HISTORY.md #64).</param>
    public void Start(bool suppressRecording = false)
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _capture = _config.Source.Type == "soundcard"
            ? new SoundcardCapture(_config.Source, _getReconnectDelaySeconds, _getReconnectMaxAttempts,
                _loggerFactory.CreateLogger("SoundcardCapture"), OnReconnectExhausted, _config.Name)
            : new StreamCapture(_ffmpegPath, _config.Source.Url, _config.Source.StreamType,
                _config.Source.AllowInvalidSsl, _config.Source.HlsBitrateIndex,
                _getReconnectDelaySeconds, _getReconnectMaxAttempts,
                _loggerFactory.CreateLogger("StreamCapture"), OnReconnectExhausted, _config.Name);

        // Wrapped in a PlayoutBuffer (docs/HISTORY.md #61) so the on-screen meter/goniometer lag
        // real time by a fixed ~12s instead of visibly freezing/snapping on every producer-side gap
        // (e.g. HLS's periodic wait for the next live segment). Recording (below) and the silence
        // detector deliberately stay on the raw, unbuffered feed.
        var meterReader = _capture.Subscribe("meter");
        _meterBuffer = new PlayoutBuffer(meterReader, _capture.SampleRate, _loggerFactory.CreateLogger("PlayoutBuffer"), _config.Name, _getPlayoutBufferSeconds());

        if (_config.RecordAudio && !suppressRecording)
        {
            var writerReader = _capture.Subscribe("writer");
            _writer = new AudioWriter(_config, writerReader, _capture.SampleRate, _capture.Channels,
                _ffmpegPath, _loggerFactory.CreateLogger("AudioWriter"));
        }

        _meter = new LevelMeter(_meterBuffer.Reader, _capture.SampleRate, _capture.Channels, _onLevelUpdate,
            _loggerFactory.CreateLogger("LevelMeter"), _onGoniometerUpdate, _config.Name);

        if (_config.SilenceDetector.Enabled)
        {
            var silenceReader = _capture.Subscribe("silence");
            _silence = new SilenceDetector(_config.SilenceDetector, silenceReader, OnSilence, OnSound,
                _loggerFactory.CreateLogger("SilenceDetector"), _config.Name);
        }

        try
        {
            _writer?.Start();
            _meterBuffer.Start();
            _meter.Start();
            _silence?.Start();
            _capture.Start();
        }
        catch
        {
            _silence?.Stop();
            _meter?.Stop();
            _meterBuffer?.Stop();
            _writer?.Stop();
            _capture?.Stop();
            _capture = null;
            _writer = null;
            _meterBuffer = null;
            _meter = null;
            _silence = null;
            throw;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorAsync(_monitorCts.Token));

        if (_config.Source.Type == "stream") StartMetadata();

        _started = true;
        EngineStatus newStatus;
        lock (_statusLock)
        {
            newStatus = new EngineStatus(IsRecording: true, IsFileRecording: _writer != null);
            _status = newStatus;
        }
        _onStatusChange(newStatus);
        log.LogInformation(_writer != null ? "Запись начата" : "Захват запущен (без записи в файл)");
    }

    public void Stop() => Stop(hasError: false);

    /// <param name="hasError">Set when the caller is <see cref="OnReconnectExhausted"/> rather than
    /// a deliberate user/UI stop — surfaces as <see cref="EngineStatus.HasError"/> so the UI can
    /// distinguish "stopped on purpose" (grey) from "gave up after too many reconnect attempts"
    /// (red) even though both end up with <c>IsRecording: false</c>.</param>
    private void Stop(bool hasError)
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _monitorCts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _monitorCts = null;
        _monitorTask = null;

        _metadataReader?.Stop(); _metadataReader = null;
        _metadataStartedAt = null;
        _metadataText = null;
        _metadataWriter?.Flush(); _metadataWriter = null;
        _silence?.Stop(); _silence = null;
        _meter?.Stop(); _meter = null;
        _meterBuffer?.Stop(); _meterBuffer = null;
        _writer?.Stop(); _writer = null;
        _capture?.Stop(); _capture = null;

        _started = false;
        EngineStatus newStatus;
        lock (_statusLock)
        {
            newStatus = new EngineStatus(IsRecording: false, HasError: hasError);
            _status = newStatus;
        }
        _onStatusChange(newStatus);
        log.LogInformation("Запись остановлена");
    }

    /// <summary>Fired by the capture backend, off its own loop thread, once it gives up after
    /// exhausting the app's reconnect-attempt budget. Logs ERROR and stops the channel with
    /// <see cref="EngineStatus.HasError"/> set — the channel stays stopped until the user (or
    /// auto_start on the next app restart) starts it again.</summary>
    private void OnReconnectExhausted()
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });
        log.LogError("Достигнут предел попыток переподключения — канал остановлен");
        Stop(hasError: true);
    }

    public void UpdateConfig(ChannelConfig newConfig)
    {
        var wasStarted = _started;
        if (!PipelineChanged(newConfig))
        {
            _config = newConfig;
            return;
        }
        Stop();
        _config = newConfig;
        if (wasStarted) Start();
    }

    /// <summary>
    /// Starts the metadata pipeline if the channel has a metadata mode configured.
    /// <see cref="ChannelConfig.Source"/>'s <c>MetadataUrl</c> field is either "" (no metadata),
    /// "icy" (ICY inline metadata — Icecast), or any other non-empty value (HLS-style JSON
    /// discovery + ffprobe ID3 fallback) — set by the "Определить наличие метаданных" button in
    /// the channel edit dialog, mirroring the legacy Python port exactly.
    /// </summary>
    private void StartMetadata()
    {
        var metaUrl = _config.Source.MetadataUrl;
        if (string.IsNullOrEmpty(metaUrl)) return;

        var metaLog = _loggerFactory.CreateLogger("Metadata");
        _metadataWriter = new MetadataWriter(_config.SavePath, _config.MetadataPath, _getAdKeywords, _getNewsKeywords);

        void OnMeta(MetadataEvent evt)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(evt.Artist)) parts.Add(evt.Artist);
            parts.Add(string.IsNullOrEmpty(evt.Title) ? evt.Raw : evt.Title);
            var text = string.Join(" - ", parts);
            metaLog.LogInformation("Метаданные: {Title}", text);
            _metadataText = text;
            _onMetadataUpdate?.Invoke(text);
            _metadataWriter?.OnMetadata(evt);
        }

        _metadataReader = metaUrl == "icy"
            ? new IcecastMetadataReader(_config.Source.Url, _config.Source.AllowInvalidSsl, OnMeta, _config.Name, metaLog)
            : new HlsMetadataReader(_config.Source.Url, _config.Source.AllowInvalidSsl, OnMeta, _config.Name, _ffprobePath, metaLog, metaUrl);

        _metadataStartedAt = DateTimeOffset.UtcNow;
        _metadataReader.Start();
        metaLog.LogInformation("Метаданные: запущен reader ({MetaUrl})", metaUrl);
    }

    /// <summary>null = no metadata URL configured (nothing to check); true = metadata detected;
    /// false = a metadata URL is configured but nothing has been detected after a grace period
    /// (long enough to cover HLS's worst-case discovery retries + ID3 fallback probe).</summary>
    private bool? ComputeMetadataOk()
    {
        if (string.IsNullOrEmpty(_config.Source.MetadataUrl)) return null;
        if (_metadataReader == null || _metadataStartedAt == null) return null;
        if (_metadataReader.HasMetadata) return true;
        return DateTimeOffset.UtcNow - _metadataStartedAt.Value >= TimeSpan.FromSeconds(MetadataGraceSeconds) ? false : null;
    }

    private bool PipelineChanged(ChannelConfig newConfig)
    {
        var old = _config;
        return old.Source.Type != newConfig.Source.Type
            || old.Source.Url != newConfig.Source.Url
            || old.Source.StreamType != newConfig.Source.StreamType
            || old.Source.AllowInvalidSsl != newConfig.Source.AllowInvalidSsl
            || old.Source.HlsBitrateIndex != newConfig.Source.HlsBitrateIndex
            || old.Source.DeviceName != newConfig.Source.DeviceName
            || old.Source.DeviceIndex != newConfig.Source.DeviceIndex
            || old.Source.DeviceUid != newConfig.Source.DeviceUid
            || old.OutputFormat.FileFormat != newConfig.OutputFormat.FileFormat
            || old.OutputFormat.Mode != newConfig.OutputFormat.Mode
            || old.OutputFormat.SampleRate != newConfig.OutputFormat.SampleRate
            || old.OutputFormat.Channels != newConfig.OutputFormat.Channels
            || old.OutputFormat.BitrateKbps != newConfig.OutputFormat.BitrateKbps
            || old.OutputFormat.BitDepth != newConfig.OutputFormat.BitDepth
            || old.SavePath != newConfig.SavePath
            || old.FileDurationMinutes != newConfig.FileDurationMinutes
            || old.DateFolderFormat != newConfig.DateFolderFormat
            || old.FileNameFormat != newConfig.FileNameFormat;
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var attempt = _capture?.ReconnectAttempt ?? 0;
                var metadataOk = ComputeMetadataOk();
                EngineStatus? newStatus = null;
                lock (_statusLock)
                {
                    if (attempt != _status.ReconnectAttempt || metadataOk != _status.MetadataOk)
                    {
                        newStatus = _status with { IsRecording = _started, ReconnectAttempt = attempt, MetadataOk = metadataOk };
                        _status = newStatus;
                    }
                }
                if (newStatus != null)
                {
                    _onStatusChange(newStatus);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnSilence()
    {
        EngineStatus newStatus;
        lock (_statusLock)
        {
            newStatus = _status with { IsSilent = true };
            _status = newStatus;
        }
        _onStatusChange(newStatus);
    }

    private void OnSound()
    {
        EngineStatus newStatus;
        lock (_statusLock)
        {
            newStatus = _status with { IsSilent = false };
            _status = newStatus;
        }
        _onStatusChange(newStatus);
    }
}
