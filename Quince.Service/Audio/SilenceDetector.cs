using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class SilenceDetector
{
    /// <summary>Fallback chunk length when the sample rate is unknown (tests calling <see cref="ProcessLevel"/> directly).</summary>
    private const double ChunkDurationApproxSeconds = 0.1;

    private enum DetectorState { Sound, Silent }

    private readonly SilenceDetectorConfig _config;
    private readonly ChannelReader<AudioChunk> _reader;
    private readonly Action _onSilence;
    private readonly Action _onSound;
    private readonly ILogger _log;
    private readonly string _channelName;
    private readonly int _sampleRate;

    private volatile DetectorState _state = DetectorState.Sound;
    private double _silenceTimer;
    private double _soundTimer;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public SilenceDetector(SilenceDetectorConfig config, ChannelReader<AudioChunk> reader, Action onSilence, Action onSound, ILogger log, string channelName = "", int sampleRate = 0)
    {
        _config = config;
        _reader = reader;
        _onSilence = onSilence;
        _onSound = onSound;
        _log = log;
        _channelName = channelName;
        _sampleRate = sampleRate;
    }

    public bool IsSilent => _state == DetectorState.Silent;
    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _state = DetectorState.Sound;
        _silenceTimer = 0;
        _soundTimer = 0;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        StopWait.Wait(_task, TimeSpan.FromSeconds(2), _log, "SilenceDetector._task");
        _cts = null;
        _task = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _reader.ReadAllAsync(ct))
            {
                // Real chunk length: capture chunk size varies with channel count/sample rate, so a fixed
                // 0.1 s per chunk skewed the trigger/resume thresholds by up to ~15%.
                var seconds = _sampleRate > 0 && chunk.Channels > 0
                    ? chunk.Samples.Length / (double)chunk.Channels / _sampleRate
                    : ChunkDurationApproxSeconds;
                ProcessLevel(ComputeLevelDb(chunk), seconds);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static double ComputeLevelDb(AudioChunk chunk)
    {
        if (chunk.Samples.Length == 0) return double.NegativeInfinity;
        var sumSq = 0.0;
        foreach (var s in chunk.Samples) sumSq += (double)s * s;
        var rms = Math.Sqrt(sumSq / chunk.Samples.Length);
        return 20.0 * Math.Log10(Math.Max(rms, 1e-9));
    }

    internal void ProcessLevel(double levelDb, double chunkSeconds = ChunkDurationApproxSeconds)
    {
        if (!_config.Enabled) return;
        var isBelow = levelDb < _config.ThresholdDbfs;

        if (_state == DetectorState.Sound)
        {
            if (isBelow)
            {
                _silenceTimer += chunkSeconds;
                if (_silenceTimer >= _config.TriggerSeconds)
                {
                    _state = DetectorState.Silent;
                    _silenceTimer = 0;
                    _soundTimer = 0;
                    _onSilence();
                    _log.LogWarning("Тишина обнаружена (уровень {Level:F1} dBFS)", levelDb);
                }
            }
            else
            {
                _silenceTimer = 0;
            }
        }
        else
        {
            if (isBelow)
            {
                _soundTimer = 0;
            }
            else
            {
                _soundTimer += chunkSeconds;
                if (_soundTimer >= _config.ResumeSeconds)
                {
                    _state = DetectorState.Sound;
                    _silenceTimer = 0;
                    _soundTimer = 0;
                    _onSound();
                    _log.LogInformation("Звук возобновился (уровень {Level:F1} dBFS)", levelDb);
                }
            }
        }
    }
}
