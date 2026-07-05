using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

public sealed class LevelMeter
{
    private const double MWindowSeconds = 0.400;
    private const double SWindowSeconds = 3.0;
    private const double UpdateIntervalSeconds = 0.100;

    // ~1 hour of integrated-loudness block history at 400ms/block (MWindowSeconds), i.e.
    // 3600s / 0.4s = 9000 blocks. Bounds memory/CPU for 24/7 operation instead of growing
    // without limit (LufsCalculator.Integrated scans the full list on every update).
    private const int MaxIntegratedBlocks = 9000;

    private readonly ChannelReader<AudioChunk> _reader;
    private readonly int _sampleRate;
    private readonly Action<LevelReading> _onUpdate;
    private readonly ILogger _log;

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

    private CancellationTokenSource? _cts;
    private Task? _task;

    public LevelMeter(ChannelReader<AudioChunk> reader, int sampleRate, int channels, Action<LevelReading> onUpdate, ILogger log)
    {
        _reader = reader;
        _sampleRate = sampleRate;
        _onUpdate = onUpdate;
        _log = log;

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
                    await Task.Delay(TimeSpan.FromSeconds(ahead - 0.1), ct);
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

        try { _onUpdate(reading); }
        catch (Exception ex) { _log.LogError(ex, "Колбэк обновления уровня выбросил исключение"); }
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
