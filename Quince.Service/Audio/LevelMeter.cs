using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

public sealed class LevelMeter
{
    private const double MWindowSeconds = 0.400;
    private const double SWindowSeconds = 3.0;

    // Diagnostics for the recurring "indicator freeze" investigation (docs/HISTORY.md #36/#52/#53):
    // the UI-side dispatcher/JS-interop instrumentation added in 0.00.043 found nothing, which
    // pointed further upstream. As of 0.00.050 the raw arrival-gap warning ("Пауза в поступлении
    // аудио-чанков") moved to PlayoutBuffer, which now normally sits in front of this class
    // (docs/HISTORY.md #61) — a gap on THIS class's own reader would just mean "the buffer is
    // priming or has run dry", not "the upstream source stalled", so logging it here would be
    // misleading (e.g. a false alarm on every channel start while the buffer primes). This
    // threshold is still used below to decide whether the pacing catch-up sleep is worth logging.
    private static readonly TimeSpan StallWarnThreshold = TimeSpan.FromMilliseconds(300);
    // Each update pushes a LevelReading through AudioEngineManager to every subscribed Blazor
    // component and triggers a StateHasChanged/render round-trip over that circuit's single SignalR
    // connection. Was cut to 0.2s (5Hz) in 0.00.008 to halve render traffic with ~5 channels
    // recording at once; restored to 0.1s (10Hz) per explicit request even though that reintroduces
    // the higher render rate — see HISTORY.md for the tradeoff if it needs revisiting.
    private const double UpdateIntervalSeconds = 0.1;
    private const int GoniometerMaxPoints = 256;

    // Some HLS sources have an inherent periodic gap in chunk delivery (waiting on the next live
    // segment — docs/HISTORY.md #54-58). As of 0.00.050 a PlayoutBuffer normally sits upstream of
    // this class and absorbs gaps shorter than its buffered depth entirely (docs/HISTORY.md #61) —
    // this decay mechanism is now mainly a fallback for the priming window (channel/listen-in start,
    // before the buffer has anything to release) and for real outages that outlast the buffer.
    // Without it, the meter would just sit dead still at its last value for the length of the gap,
    // which reads as "frozen"/broken even though it's momentary. Once real updates stop arriving for
    // DecayGraceWindow, a timer synthesizes readings that fall toward silence at
    // DecayRateDbPerSecond — like a real analog meter's ballistic release — so the meter visibly
    // (and correctly) settles instead of freezing; a real reading, whenever it resumes, simply
    // supersedes the decayed one. Pure cosmetic smoothing — does not touch the underlying gap.
    private static readonly TimeSpan DecayGraceWindow = TimeSpan.FromMilliseconds(250);
    private const double DecayRateDbPerSecond = 3.0;
    private const double DecayFloorDb = -60.0;

    // ~1 hour of integrated-loudness block history at 400ms/block (MWindowSeconds), i.e.
    // 3600s / 0.4s = 9000 blocks. Bounds memory/CPU for 24/7 operation instead of growing
    // without limit (LufsCalculator.Integrated scans the full list on every update).
    private const int MaxIntegratedBlocks = 9000;

    private readonly ChannelReader<AudioChunk> _reader;
    private readonly int _sampleRate;
    private readonly Action<LevelReading> _onUpdate;
    private readonly Action<GoniometerFrame>? _onGoniometerUpdate;
    private readonly ILogger _log;
    private readonly string _channelName;

    private readonly KWeightingFilter _kWeight;
    private readonly double[][] _mBuf;
    private readonly double[][] _sBuf;
    private int _mHead, _sHead;
    private readonly int _blockSize;

    private readonly List<double> _blockLufs = new();
    private readonly List<double> _blockTotalMs = new();
    private readonly double[] _blockAccum;
    private int _blockSamples;

    private int _samplesSinceUpdate;
    private readonly int _updateEvery;
    private double _lastUpdateTime;

    private readonly object _peakLock = new();
    private double _truePeakMaxDb = double.NegativeInfinity;

    private readonly object _readingLock = new();
    private LevelReading _lastReading = new();
    private long _lastRealUpdateAt = Stopwatch.GetTimestamp();
    private Timer? _decayTimer;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public LevelMeter(ChannelReader<AudioChunk> reader, int sampleRate, int channels, Action<LevelReading> onUpdate, ILogger log,
        Action<GoniometerFrame>? onGoniometerUpdate = null, string channelName = "")
    {
        _reader = reader;
        _sampleRate = sampleRate;
        _onUpdate = onUpdate;
        _onGoniometerUpdate = onGoniometerUpdate;
        _log = log;
        _channelName = channelName;

        _kWeight = new KWeightingFilter(sampleRate);
        var mCap = (int)Math.Ceiling(MWindowSeconds * sampleRate);
        var sCap = (int)Math.Ceiling(SWindowSeconds * sampleRate);
        _mBuf = CreateBuffer(mCap, channels);
        _sBuf = CreateBuffer(sCap, channels);
        _blockSize = mCap;
        _blockAccum = new double[channels];
        _updateEvery = (int)(UpdateIntervalSeconds * sampleRate);
    }

    private static double[][] CreateBuffer(int capacity, int channels)
    {
        var buf = new double[channels][];
        for (var c = 0; c < channels; c++) buf[c] = new double[capacity];
        return buf;
    }

    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
        var period = TimeSpan.FromSeconds(UpdateIntervalSeconds);
        _decayTimer = new Timer(DecayTick, null, period, period);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
        _decayTimer?.Dispose();
        _decayTimer = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var realStart = DateTime.UtcNow;
        var audioSeconds = 0.0;
        try
        {
            await foreach (var chunk in _reader.ReadAllAsync(ct))
            {
                try
                {
                    ProcessChunk(chunk);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Ошибка обработки чанка в LevelMeter");
                    continue;
                }

                audioSeconds += chunk.FrameCount / (double)_sampleRate;
                var ahead = audioSeconds - (DateTime.UtcNow - realStart).TotalSeconds;
                if (ahead > 0.2)
                {
                    // Draining a backlog built up during a gap above catches audioSeconds up to (or
                    // past) real time almost instantly, since queued chunks are yielded back-to-back
                    // with no per-item delay — this then sleeps to resync to real time, which can
                    // roughly double the gap's visible effect on _onUpdate (no reading fires for the
                    // ORIGINAL gap, then none fire for this correction either).
                    var sleepFor = TimeSpan.FromSeconds(ahead - 0.1);
                    if (sleepFor >= StallWarnThreshold)
                        _log.LogWarning("Пауза синхронизации темпа LevelMeter: {SleepMs:F0}мс (обработка опередила реальное время на {AheadMs:F0}мс)", sleepFor.TotalMilliseconds, ahead * 1000);
                    await Task.Delay(sleepFor, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void ProcessChunk(AudioChunk chunk)
    {
        var frames = chunk.FrameCount;
        if (frames == 0) return;
        var channels = chunk.Channels;

        var perChannel = new double[channels][];
        for (var c = 0; c < channels; c++)
        {
            perChannel[c] = new double[frames];
            for (var f = 0; f < frames; f++)
                perChannel[c][f] = chunk.Samples[f * channels + c];
        }

        // True Peak on the original (un-weighted) PCM.
        var tpL = TruePeakCalculator.TruePeakDb(perChannel[0]);
        var tpR = channels >= 2 ? TruePeakCalculator.TruePeakDb(perChannel[1]) : double.NegativeInfinity;
        var tpOverall = channels >= 2 ? Math.Max(tpL, tpR) : tpL;

        lock (_peakLock)
        {
            if (tpOverall > _truePeakMaxDb) _truePeakMaxDb = tpOverall;
        }

        if (_onGoniometerUpdate != null)
        {
            try
            {
                var right = channels >= 2 ? perChannel[1] : perChannel[0];
                _onGoniometerUpdate(GoniometerFrame.Decimate(perChannel[0], right, GoniometerMaxPoints));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Колбэк обновления гониометра выбросил исключение");
            }
        }

        // K-weight in place (mutates perChannel) for loudness measurement only.
        _kWeight.Apply(perChannel);

        WriteRing(_mBuf, ref _mHead, perChannel, frames);
        WriteRing(_sBuf, ref _sHead, perChannel, frames);
        AccumulateIntegratedBlocks(perChannel, frames);

        _samplesSinceUpdate += frames;
        if (_samplesSinceUpdate < _updateEvery) return;
        _samplesSinceUpdate = 0;

        var now = Environment.TickCount64 / 1000.0;
        if (now - _lastUpdateTime < UpdateIntervalSeconds * 0.5) return;
        _lastUpdateTime = now;

        var msM = RingMeanSquareTotal(_mBuf);
        var msS = RingMeanSquareTotal(_sBuf);
        double peakMax;
        lock (_peakLock) { peakMax = _truePeakMaxDb; }

        var reading = new LevelReading(
            TruePeakDb: tpOverall,
            TruePeakMaxDb: peakMax,
            LoudnessM: LufsCalculator.FromMeanSquare(msM),
            LoudnessS: LufsCalculator.FromMeanSquare(msS),
            LoudnessI: LufsCalculator.Integrated(_blockLufs, _blockTotalMs),
            TruePeakLDb: tpL,
            TruePeakRDb: tpR);

        lock (_readingLock)
        {
            _lastReading = reading;
            _lastRealUpdateAt = Stopwatch.GetTimestamp();
        }

        try { _onUpdate(reading); }
        catch (Exception ex) { _log.LogError(ex, "Колбэк обновления уровня выбросил исключение"); }
    }

    /// <summary>Fires on a timer independent of chunk arrival — see the class-level comment on
    /// <see cref="DecayGraceWindow"/> for why. No-ops whenever real chunks are still flowing.</summary>
    internal void DecayTick(object? _)
    {
        LevelReading decayed;
        lock (_readingLock)
        {
            if (Stopwatch.GetElapsedTime(_lastRealUpdateAt) < DecayGraceWindow) return;
            if (IsFullyDecayed(_lastReading)) return;
            decayed = Decay(_lastReading, TimeSpan.FromSeconds(UpdateIntervalSeconds));
            _lastReading = decayed;
        }

        try { _onUpdate(decayed); }
        catch (Exception ex) { _log.LogError(ex, "Колбэк обновления уровня выбросил исключение (затухание)"); }
    }

    internal static bool IsFullyDecayed(LevelReading r) =>
        double.IsNegativeInfinity(r.TruePeakDb) && double.IsNegativeInfinity(r.LoudnessM) && double.IsNegativeInfinity(r.LoudnessS);

    internal static LevelReading Decay(LevelReading last, TimeSpan tick)
    {
        var dropDb = DecayRateDbPerSecond * tick.TotalSeconds;
        return last with
        {
            TruePeakDb = DecayValue(last.TruePeakDb, dropDb),
            LoudnessM = DecayValue(last.LoudnessM, dropDb),
            LoudnessS = DecayValue(last.LoudnessS, dropDb),
            TruePeakLDb = DecayValue(last.TruePeakLDb, dropDb),
            TruePeakRDb = DecayValue(last.TruePeakRDb, dropDb),
            // LoudnessI is a long-window (~seconds-to-hours) integrated average and TruePeakMaxDb is
            // a sticky peak-hold — a momentary gap in incoming audio shouldn't visibly move either.
        };
    }

    internal static double DecayValue(double db, double dropDb)
    {
        if (double.IsNegativeInfinity(db)) return db;
        var next = db - dropDb;
        return next <= DecayFloorDb ? double.NegativeInfinity : next;
    }

    private static void WriteRing(double[][] buf, ref int head, double[][] perChannel, int frames)
    {
        var cap = buf[0].Length;
        if (frames >= cap)
        {
            for (var c = 0; c < buf.Length; c++)
                Array.Copy(perChannel[c], frames - cap, buf[c], 0, cap);
            head = 0;
            return;
        }

        var end = head + frames;
        for (var c = 0; c < buf.Length; c++)
        {
            if (end <= cap)
            {
                Array.Copy(perChannel[c], 0, buf[c], head, frames);
            }
            else
            {
                var first = cap - head;
                Array.Copy(perChannel[c], 0, buf[c], head, first);
                Array.Copy(perChannel[c], first, buf[c], 0, frames - first);
            }
        }
        head = end % cap;
    }

    private static double RingMeanSquareTotal(double[][] buf)
    {
        var total = 0.0;
        foreach (var chBuf in buf)
        {
            var sumSq = 0.0;
            foreach (var v in chBuf) sumSq += v * v;
            total += sumSq / chBuf.Length;
        }
        return total;
    }

    private void AccumulateIntegratedBlocks(double[][] perChannel, int frames)
    {
        var channels = perChannel.Length;
        var offset = 0;
        var remaining = frames;
        while (remaining > 0)
        {
            var space = _blockSize - _blockSamples;
            var take = Math.Min(remaining, space);
            for (var c = 0; c < channels; c++)
            {
                var sumSq = 0.0;
                for (var f = offset; f < offset + take; f++)
                    sumSq += perChannel[c][f] * perChannel[c][f];
                _blockAccum[c] += sumSq;
            }
            _blockSamples += take;
            offset += take;
            remaining -= take;

            if (_blockSamples >= _blockSize)
            {
                var totalMs = 0.0;
                for (var c = 0; c < channels; c++) totalMs += _blockAccum[c] / _blockSize;
                _blockLufs.Add(LufsCalculator.FromMeanSquare(totalMs));
                _blockTotalMs.Add(totalMs);
                if (_blockLufs.Count > MaxIntegratedBlocks)
                {
                    _blockLufs.RemoveAt(0);
                    _blockTotalMs.RemoveAt(0);
                }
                Array.Clear(_blockAccum);
                _blockSamples = 0;
            }
        }
    }
}
