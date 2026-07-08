using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class SilenceDetector
{
    private const double ChunkDurationApproxSeconds = 0.1;

    private readonly SilenceDetectorConfig _config;
    private readonly ChannelReader<AudioChunk> _reader;
    private readonly Action _onSilence;
    private readonly Action _onSound;
    private readonly ILogger _log;
    private readonly string _channelName;

    private string _state = "SOUND";
    private double _silenceTimer;
    private double _soundTimer;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public SilenceDetector(SilenceDetectorConfig config, ChannelReader<AudioChunk> reader, Action onSilence, Action onSound, ILogger log, string channelName = "")
    {
        _config = config;
        _reader = reader;
        _onSilence = onSilence;
        _onSound = onSound;
        _log = log;
        _channelName = channelName;
    }

    public bool IsSilent => _state == "SILENT";
    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _state = "SOUND";
        _silenceTimer = 0;
        _soundTimer = 0;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _reader.ReadAllAsync(ct))
            {
                ProcessLevel(ComputeLevelDb(chunk));
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

    internal void ProcessLevel(double levelDb)
    {
        if (!_config.Enabled) return;
        var isBelow = levelDb < _config.ThresholdDbfs;

        if (_state == "SOUND")
        {
            if (isBelow)
            {
                _silenceTimer += ChunkDurationApproxSeconds;
                if (_silenceTimer >= _config.TriggerSeconds)
                {
                    _state = "SILENT";
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
                _soundTimer += ChunkDurationApproxSeconds;
                if (_soundTimer >= _config.ResumeSeconds)
                {
                    _state = "SOUND";
                    _silenceTimer = 0;
                    _soundTimer = 0;
                    _onSound();
                    _log.LogInformation("Звук возобновился (уровень {Level:F1} dBFS)", levelDb);
                }
            }
        }
    }
}
