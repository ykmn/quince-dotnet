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
/// this window every subscriber sees no chunks at all, which <see cref="LevelMeter"/>'s own
/// existing decay-to-silence handles gracefully with no special-casing needed here). Once primed,
/// releases chunks paced to real wall-clock time regardless of how unevenly they arrive upstream —
/// e.g. HLS's periodic wait for the next live segment (docs/HISTORY.md #54-58) — so a producer-side
/// gap shorter than the buffered depth becomes invisible to every subscriber. The cost is a fixed
/// added latency equal to the buffered depth: subscribers always lag the real feed by that much.
/// If a real outage lasts longer than the buffered depth, the queue simply runs dry and this behaves
/// like the unbuffered feed again (no exception, no special handling — degrades gracefully).
///
/// Supports multiple simultaneous subscribers (<see cref="Subscribe"/>/<see cref="Unsubscribe"/>,
/// mirroring <see cref="FfmpegPipedCapture"/>'s own raw-feed fan-out) so a channel's meter and any
/// number of browser "listen in" HTTP requests can share the exact same already-primed, already-paced
/// instance instead of each needing to prime its own — a late-joining subscriber (e.g. a listen-in
/// click on a channel that's been running for hours) starts receiving live-paced chunks immediately,
/// with no backfill of chunks already released to earlier subscribers.
///
/// <c>TargetDelaySeconds</c> itself is source-aware since docs/HISTORY.md #126: the periodic gap
/// this class exists to hide has only ever been observed on HLS sources, so
/// <see cref="ChannelEngine.Start"/> only sizes it generously for HLS channels (from that channel's
/// own measured playlist segment duration via <see cref="HlsSegmentDurationService"/>) and uses a
/// small fixed delay for everything else (Icecast/soundcard/Livewire). This class itself is
/// unchanged — <c>_targetDelaySeconds</c> is deliberately still constructor-only/immutable, not
/// resizable on a live instance: safely growing/shrinking an already-primed buffer mid-flight
/// (without releasing stale data early or deadlocking against an already-exceeded threshold) is
/// meaningfully riskier than the alternative of simply re-resolving the delay the next time the
/// owning channel restarts (which <see cref="ChannelEngine.PipelineChanged"/> already does on any
/// relevant config change).
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

    // Bounded with DropOldest — NOT unbounded — for a specific reason (docs/HISTORY.md #64): a
    // subscriber (LevelMeter's loop, or AudioStreamEndpoint's ffmpeg-encode-then-HTTP-write-to-browser
    // chain) can momentarily fall behind real time for reasons entirely outside this class's control
    // (a GC pause, ffmpeg process startup, the browser's own pre-buffering before it starts an
    // <audio> stream, backpressure from a slow client network write). With an unbounded channel, any
    // such stall queues up an ever-growing backlog that's never trimmed — the consumer eventually
    // catches up but keeps replaying stale data forever, turning one transient hiccup into a
    // permanent, compounding lag (this is what caused browser listen-in audio to drift tens of
    // seconds behind the meter in the field). Capacity ~30 chunks (~3s at the ~93-100ms chunk size
    // used throughout this pipeline) is enough slack to absorb ordinary scheduling jitter without
    // dropping anything, while still bounding how far behind real time the output can silently get —
    // once exceeded, the oldest not-yet-consumed chunk is dropped in favor of the newest, so the
    // stream self-corrects back toward the intended TargetDelaySeconds lag instead of drifting
    // further with every hiccup. Same reasoning applies to every subscriber equally (meter and
    // listen-in are both "a human is watching/listening in real time" cases), so every subscriber's
    // channel uses this same capacity/DropOldest combination.
    private const int OutputCapacity = 30;

    // Multi-consumer fan-out, mirroring FfmpegPipedCapture's own _consumers dictionary: each
    // subscriber gets its own bounded output channel so one slow subscriber falling behind and
    // dropping chunks never affects any other subscriber.
    private readonly object _consumerLock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

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

    /// <summary>Registers a new subscriber under <paramref name="consumerId"/> and returns its own
    /// bounded reader. Only chunks released (see <see cref="ReleaseDue"/>) after this call are ever
    /// written to it — no backfill of chunks already released to earlier subscribers.</summary>
    public ChannelReader<AudioChunk> Subscribe(string consumerId)
    {
        var channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(OutputCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_consumerLock) { _consumers[consumerId] = channel.Writer; }
        return channel.Reader;
    }

    /// <summary>Removes a subscriber. Its reader is not completed — same "reader relies on its own
    /// CancellationToken to unwind" contract <see cref="FfmpegPipedCapture.Unsubscribe"/> already
    /// uses, since a caller that's unsubscribing already knows it's done with the reader.</summary>
    public void Unsubscribe(string consumerId)
    {
        lock (_consumerLock) { _consumers.Remove(consumerId); }
    }

    /// <summary>Whether priming has completed (see <see cref="Enqueue"/>) — exposed so a consumer
    /// like <see cref="Services.AudioStreamEndpoint"/> can tell "no chunks ever arrived at all" apart
    /// from "primed fine, something downstream of this buffer is the problem" when diagnosing a
    /// silently-failing listen-in stream.</summary>
    public bool Primed { get { lock (_queueLock) return _primed; } }

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
                    _log.LogDebug("Пауза в поступлении аудио-чанков ({Channel}): {GapMs:F0}мс с предыдущего чанка", _channelName, gap.TotalMilliseconds);

                Enqueue(chunk);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Complete every currently-attached subscriber's writer so a listen-in HTTP request's
            // ReadAllAsync loop ends via normal completion (clean stream end) instead of hanging
            // until its own CancellationToken eventually fires, whenever the channel stops/restarts.
            List<ChannelWriter<AudioChunk>> writers;
            lock (_consumerLock) { writers = _consumers.Values.ToList(); }
            foreach (var writer in writers) writer.TryComplete();
        }
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
        bool justPrimed;
        double queuedAtPrime = 0;
        lock (_queueLock)
        {
            _queue.Enqueue(chunk);
            _queuedSeconds += chunk.FrameCount / (double)_sampleRate;
            justPrimed = !_primed && _queuedSeconds >= _targetDelaySeconds;
            if (justPrimed)
            {
                _primed = true;
                _releaseAnchor = Stopwatch.GetTimestamp();
                queuedAtPrime = _queuedSeconds;
            }
        }
        // Logged outside the lock (ILogger calls shouldn't hold it) — the only "this consumer is
        // actually getting real audio" signal this class previously emitted anywhere, at any level.
        // Its absence in the log for a given listen-in attempt now directly means "no chunks ever
        // reached this PlayoutBuffer instance", not just "audio started but sounds off" — see also
        // the "never primed" watchdog in AudioStreamEndpoint, which catches the case where Enqueue
        // is never even called.
        if (justPrimed)
            _log.LogInformation("PlayoutBuffer «{Channel}»: буфер прогрет ({Queued:F1}с накоплено, цель {Target:F1}с)",
                _channelName, queuedAtPrime, _targetDelaySeconds);
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

        List<ChannelWriter<AudioChunk>> writers;
        lock (_consumerLock) { writers = _consumers.Values.ToList(); }
        foreach (var chunk in toRelease)
            foreach (var writer in writers)
                writer.TryWrite(chunk);
    }
}
