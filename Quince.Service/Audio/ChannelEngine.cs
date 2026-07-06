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
    private readonly Func<IReadOnlyList<string>>? _getAdKeywords;

    private ChannelConfig _config;
    private IAudioCapture? _capture;
    private AudioWriter? _writer;
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
        Func<IReadOnlyList<string>>? getAdKeywords = null)
    {
        _config = config;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _loggerFactory = loggerFactory;
        _onLevelUpdate = onLevelUpdate;
        _onStatusChange = onStatusChange;
        _onGoniometerUpdate = onGoniometerUpdate;
        _getAdKeywords = getAdKeywords;
    }

    public EngineStatus Status
    {
        get { lock (_statusLock) { return _status; } }
    }
    public ChannelConfig Config => _config;

    /// <summary>Exposes the running channel's raw audio to an extra consumer (e.g. output monitoring
    /// playback) alongside the meter/writer/silence-detector consumers already subscribed internally.
    /// Null if the channel isn't currently running.</summary>
    public System.Threading.Channels.ChannelReader<AudioChunk>? Subscribe(string consumerId) => _capture?.Subscribe(consumerId);

    public void Unsubscribe(string consumerId) => _capture?.Unsubscribe(consumerId);

    public void Start()
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _capture = _config.Source.Type == "soundcard"
            ? new SoundcardCapture(_config.Source, _config.Source.ReconnectDelaySeconds,
                _loggerFactory.CreateLogger("SoundcardCapture"))
            : new StreamCapture(_ffmpegPath, _config.Source.Url, _config.Source.StreamType,
                _config.Source.AllowInvalidSsl, _config.Source.HlsBitrateIndex, _config.Source.ReconnectDelaySeconds,
                _loggerFactory.CreateLogger("StreamCapture"));

        var meterReader = _capture.Subscribe("meter");

        if (_config.RecordAudio)
        {
            var writerReader = _capture.Subscribe("writer");
            _writer = new AudioWriter(_config, writerReader, _capture.SampleRate, _capture.Channels,
                _ffmpegPath, _loggerFactory.CreateLogger("AudioWriter"));
        }

        _meter = new LevelMeter(meterReader, _capture.SampleRate, _capture.Channels, _onLevelUpdate,
            _loggerFactory.CreateLogger("LevelMeter"), _onGoniometerUpdate);

        if (_config.SilenceDetector.Enabled)
        {
            var silenceReader = _capture.Subscribe("silence");
            _silence = new SilenceDetector(_config.SilenceDetector, silenceReader, OnSilence, OnSound,
                _loggerFactory.CreateLogger("SilenceDetector"));
        }

        try
        {
            _writer?.Start();
            _meter.Start();
            _silence?.Start();
            _capture.Start();
        }
        catch
        {
            _silence?.Stop();
            _meter?.Stop();
            _writer?.Stop();
            _capture?.Stop();
            _capture = null;
            _writer = null;
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
            newStatus = new EngineStatus(IsRecording: true);
            _status = newStatus;
        }
        _onStatusChange(newStatus);
        log.LogInformation("Запись начата");
    }

    public void Stop()
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _monitorCts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _monitorCts = null;
        _monitorTask = null;

        _metadataReader?.Stop(); _metadataReader = null;
        _metadataStartedAt = null;
        _metadataWriter?.Flush(); _metadataWriter = null;
        _silence?.Stop(); _silence = null;
        _meter?.Stop(); _meter = null;
        _writer?.Stop(); _writer = null;
        _capture?.Stop(); _capture = null;

        _started = false;
        EngineStatus newStatus;
        lock (_statusLock)
        {
            newStatus = new EngineStatus(IsRecording: false);
            _status = newStatus;
        }
        _onStatusChange(newStatus);
        log.LogInformation("Запись остановлена");
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
        _metadataWriter = new MetadataWriter(_config.SavePath, _config.MetadataPath, _getAdKeywords);

        void OnMeta(MetadataEvent evt)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(evt.Artist)) parts.Add(evt.Artist);
            parts.Add(string.IsNullOrEmpty(evt.Title) ? evt.Raw : evt.Title);
            metaLog.LogInformation("Метаданные: {Title}", string.Join(" - ", parts));
            _metadataWriter?.OnMetadata(evt);
        }

        _metadataReader = metaUrl == "icy"
            ? new IcecastMetadataReader(_config.Source.Url, _config.Source.AllowInvalidSsl, OnMeta, _config.Name, metaLog)
            : new HlsMetadataReader(_config.Source.Url, _config.Source.AllowInvalidSsl, OnMeta, _config.Name, _ffprobePath, metaLog);

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
