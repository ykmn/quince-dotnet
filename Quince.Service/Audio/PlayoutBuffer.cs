using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>
/// Smooths arrival-time gaps in a channel's raw audio feed for consumers a human directly watches or
/// listens to (the level meter, browser "listen in" playback) — deliberately NOT used for the
/// recording pipeline (<see cref="AudioWriter"/>) or <see cref="SilenceDetector"/>, which must stay on
/// the untouched live feed (see docs/HISTORY.md #61 for why: recording integrity/timing shouldn't
/// depend on a buffer, and silence alerting needs to stay prompt, not smoothed).
///
/// Buffers up to <c>TargetDelaySeconds</c> of audio before releasing anything ("priming" — during
/// this window the wrapped consumer sees no chunks at all, which <see cref="LevelMeter"/>'s own
/// existing decay-to-silence handles gracefully with no special-casing needed here). Once primed,
/// releases chunks paced to real wall-clock time regardless of how unevenly they arrive upstream —
/// e.g. HLS's periodic wait for the next live segment (docs/HISTORY.md #54-58) — so a producer-side
/// gap shorter than the buffered depth becomes invisible to the consumer. The cost is a fixed added
/// latency equal to the buffered depth: the wrapped consumer always lags the real feed by that much.
/// If a real outage lasts longer than the buffered depth, the queue simply runs dry and this behaves
/// like the unbuffered feed again (no exception, no special handling — degrades gracefully).
/// </summary>
public sealed class PlayoutBuffer
{
    public const double DefaultTargetDelaySeconds = 12.0;

    // Same threshold family as the pre-buffer "Пауза в поступлении..." warning this replaces (moved
    // here since this is now the point where a raw upstream gap is first observed) — raised from the
    // previous 300ms because that fired almost continuously for channels with routine ~300-500ms
    // network jitter, burying genuinely large stalls in noise.
    private static readonly TimeSpan RawGapWarnThreshold = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan ReleaseTickInterval = TimeSpan.FromMilliseconds(50);

    private readonly ChannelReader<AudioChunk> _source;
    private readonly int _sampleRate;
    private readonly ILogger _log;
    private readonly string _channelName;
    private readonly double _targetDelaySeconds;
    private readonly Channel<AudioChunk> _output = Channel.CreateUnbounded<AudioChunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly object _queueLock = new();
    private readonly Queue<AudioChunk> _queue = new();
    private double _queuedSeconds;
    private bool _primed;
    private long _releaseAnchor;
    private double _releasedSeconds;

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Task? _releaseTask;

    public PlayoutBuffer(ChannelReader<AudioChunk> source, int sampleRate, ILogger log, string channelName = "",
        double targetDelaySeconds = DefaultTargetDelaySeconds)
    {
        _source = source;
        _sampleRate = sampleRate;
        _log = log;
        _channelName = channelName;
        _targetDelaySeconds = targetDelaySeconds;
    }

    public ChannelReader<AudioChunk> Reader => _output.Reader;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
        _releaseTask = Task.Run(() => ReleaseLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        var tasks = new[] { _pumpTask, _releaseTask }.Where(t => t != null).Select(t => t!).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts = null;
        _pumpTask = null;
        _releaseTask = null;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var lastChunkAt = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var chunk in _source.ReadAllAsync(ct))
            {
                var gap = Stopwatch.GetElapsedTime(lastChunkAt);
                lastChunkAt = Stopwatch.GetTimestamp();
                if (gap >= RawGapWarnThreshold)
                    _log.LogDebug("Пауза в поступлении аудио-чанков: {GapMs:F0}мс с предыдущего чанка", gap.TotalMilliseconds);

                Enqueue(chunk);
            }
        }
        catch (OperationCanceledException) { }
        finally { _output.Writer.TryComplete(); }
    }

    private async Task ReleaseLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReleaseTickInterval, ct);
                ReleaseDue();
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Enqueue(AudioChunk chunk)
    {
        lock (_queueLock)
        {
            _queue.Enqueue(chunk);
            _queuedSeconds += chunk.FrameCount / (double)_sampleRate;
            if (!_primed && _queuedSeconds >= _targetDelaySeconds)
            {
                _primed = true;
                _releaseAnchor = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>Releases every queued chunk whose scheduled playout time (real time elapsed since
    /// priming completed) has arrived. No-ops before priming or once the queue has drained dry (a
    /// real outage longer than the buffered depth) — both are expected, not error conditions.</summary>
    internal void ReleaseDue()
    {
        List<AudioChunk>? toRelease = null;
        lock (_queueLock)
        {
            if (!_primed) return;
            var elapsed = Stopwatch.GetElapsedTime(_releaseAnchor).TotalSeconds;
            while (_queue.Count > 0)
            {
                var nextDuration = _queue.Peek().FrameCount / (double)_sampleRate;
                if (_releasedSeconds + nextDuration > elapsed) break;
                var chunk = _queue.Dequeue();
                _releasedSeconds += nextDuration;
                (toRelease ??= new List<AudioChunk>()).Add(chunk);
            }
        }
        if (toRelease == null) return;
        foreach (var chunk in toRelease) _output.Writer.TryWrite(chunk);
    }
}
