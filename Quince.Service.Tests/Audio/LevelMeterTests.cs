using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using System.Threading.Channels;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class LevelMeterTests
{
    [Fact]
    public void ProcessChunk_FullScaleMono_ReportsZeroDbTruePeak()
    {
        LevelReading? received = null;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 1,
            onUpdate: r => received = r, log: NullLogger.Instance);

        // 44100 * 0.1 = 4410 samples needed to cross the ~100ms update threshold.
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1.0f;
        var chunk = new AudioChunk(samples, channels: 1);

        meter.ProcessChunk(chunk);

        Assert.NotNull(received);
        Assert.Equal(0.0, received!.TruePeakDb, precision: 6);
        Assert.Equal(0.0, received.TruePeakMaxDb, precision: 6);
    }

    [Fact]
    public void ProcessChunk_BelowUpdateThreshold_DoesNotFireCallback()
    {
        LevelReading? received = null;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 1,
            onUpdate: r => received = r, log: NullLogger.Instance);

        var chunk = new AudioChunk(new float[] { 1.0f, 1.0f, 1.0f }, channels: 1);
        meter.ProcessChunk(chunk);

        Assert.Null(received);
    }

    [Fact]
    public void ProcessChunk_Stereo_FiresGoniometerCallbackWithRawPreWeightedSamples()
    {
        GoniometerFrame? received = null;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 2,
            onUpdate: _ => { }, log: NullLogger.Instance,
            onGoniometerUpdate: f => received = f);

        // Interleaved stereo: L is a ramp, R is the negated ramp, so we can check the raw
        // (pre-K-weighted) values survive the round trip through the decimation callback.
        const int frames = 4096;
        var samples = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var l = f / (float)frames;
            samples[f * 2] = l;
            samples[f * 2 + 1] = -l;
        }
        var chunk = new AudioChunk(samples, channels: 2);

        meter.ProcessChunk(chunk);

        Assert.NotNull(received);
        Assert.True(received!.Left.Length <= 256);
        Assert.Equal(received.Left.Length, received.Right.Length);
        Assert.True(received.Left.Length > 0);

        // Values must come from the actual input, not be zeroed/garbage.
        for (var i = 0; i < received.Left.Length; i++)
        {
            Assert.Equal(-received.Left[i], received.Right[i], precision: 5);
            Assert.InRange(received.Left[i], 0.0f, 1.0f);
        }
        // At least one non-zero sample proves it's not all-zeroed (the ramp starts at 0).
        Assert.Contains(received.Left, v => v > 0);
    }

    [Fact]
    public void DecayTick_ImmediatelyAfterRealUpdate_DoesNotFire()
    {
        var callCount = 0;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 1,
            onUpdate: _ => callCount++, log: NullLogger.Instance);

        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1.0f;
        meter.ProcessChunk(new AudioChunk(samples, channels: 1));
        Assert.Equal(1, callCount);

        meter.DecayTick(null);

        // Grace window (250ms) hasn't elapsed since the real update above — no synthetic tick.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task DecayTick_AfterGraceWindowElapsed_FiresReadingBelowLastRealValue()
    {
        LevelReading? received = null;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 1,
            onUpdate: r => received = r, log: NullLogger.Instance);

        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1.0f;
        meter.ProcessChunk(new AudioChunk(samples, channels: 1));
        var initialPeak = received!.TruePeakDb;

        await Task.Delay(300);
        meter.DecayTick(null);

        Assert.True(received!.TruePeakDb < initialPeak);
    }

    [Fact]
    public void DecayValue_DropsByGivenAmount()
    {
        Assert.Equal(-10.0, LevelMeter.DecayValue(-5.0, 5.0), precision: 6);
    }

    [Fact]
    public void DecayValue_NegativeInfinity_StaysNegativeInfinity()
    {
        Assert.True(double.IsNegativeInfinity(LevelMeter.DecayValue(double.NegativeInfinity, 5.0)));
    }

    [Fact]
    public void DecayValue_PastFloor_SnapsToNegativeInfinity()
    {
        Assert.True(double.IsNegativeInfinity(LevelMeter.DecayValue(-58.0, 5.0)));
    }

    [Fact]
    public void IsFullyDecayed_AllRelevantFieldsNegativeInfinity_ReturnsTrue()
    {
        var reading = new LevelReading(
            TruePeakDb: double.NegativeInfinity, TruePeakMaxDb: -3.0,
            LoudnessM: double.NegativeInfinity, LoudnessS: double.NegativeInfinity,
            LoudnessI: -12.0, TruePeakLDb: -3.0, TruePeakRDb: -3.0);

        // TruePeakMaxDb/LoudnessI/TruePeakLDb/TruePeakRDb intentionally don't gate this — only the
        // fields Decay() actually reduces do.
        Assert.True(LevelMeter.IsFullyDecayed(reading));
    }
}
