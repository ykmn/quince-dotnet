using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class ChannelEngine
{
    private readonly string _ffmpegPath;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<LevelReading> _onLevelUpdate;
    private readonly Action<EngineStatus> _onStatusChange;

    private ChannelConfig _config;
    private StreamCapture? _capture;
    private AudioWriter? _writer;
    private LevelMeter? _meter;
    private SilenceDetector? _silence;
    private EngineStatus _status = new();
    private bool _started;
    private readonly object _statusLock = new();

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public ChannelEngine(ChannelConfig config, string ffmpegPath, ILoggerFactory loggerFactory,
        Action<LevelReading> onLevelUpdate, Action<EngineStatus> onStatusChange)
    {
        _config = config;
        _ffmpegPath = ffmpegPath;
        _loggerFactory = loggerFactory;
        _onLevelUpdate = onLevelUpdate;
        _onStatusChange = onStatusChange;
    }

    public EngineStatus Status
    {
        get { lock (_statusLock) { return _status; } }
    }
    public ChannelConfig Config => _config;

    public void Start()
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _capture = new StreamCapture(_ffmpegPath, _config.Source.Url, _config.Source.StreamType,
            _config.Source.AllowInvalidSsl, _config.Source.HlsBitrateIndex, _config.Source.ReconnectDelaySeconds,
            _loggerFactory.CreateLogger("StreamCapture"));

        var meterReader = _capture.Subscribe("meter");

        if (_config.RecordAudio)
        {
            var writerReader = _capture.Subscribe("writer");
            _writer = new AudioWriter(_config, writerReader, StreamCapture.SampleRate, StreamCapture.Channels,
                _ffmpegPath, _loggerFactory.CreateLogger("AudioWriter"));
        }

        _meter = new LevelMeter(meterReader, StreamCapture.SampleRate, StreamCapture.Channels, _onLevelUpdate,
            _loggerFactory.CreateLogger("LevelMeter"));

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

    private bool PipelineChanged(ChannelConfig newConfig)
    {
        var old = _config;
        return old.Source.Url != newConfig.Source.Url
            || old.Source.StreamType != newConfig.Source.StreamType
            || old.Source.AllowInvalidSsl != newConfig.Source.AllowInvalidSsl
            || old.Source.HlsBitrateIndex != newConfig.Source.HlsBitrateIndex
            || old.OutputFormat.FileFormat != newConfig.OutputFormat.FileFormat
            || old.OutputFormat.Mode != newConfig.OutputFormat.Mode
            || old.OutputFormat.SampleRate != newConfig.OutputFormat.SampleRate
            || old.OutputFormat.Channels != newConfig.OutputFormat.Channels
            || old.OutputFormat.BitrateKbps != newConfig.OutputFormat.BitrateKbps
            || old.OutputFormat.BitDepth != newConfig.OutputFormat.BitDepth
            || old.SavePath != newConfig.SavePath
            || old.FileDurationSeconds != newConfig.FileDurationSeconds
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
                EngineStatus? newStatus = null;
                lock (_statusLock)
                {
                    if (attempt != _status.ReconnectAttempt)
                    {
                        newStatus = _status with { IsRecording = _started, ReconnectAttempt = attempt };
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
