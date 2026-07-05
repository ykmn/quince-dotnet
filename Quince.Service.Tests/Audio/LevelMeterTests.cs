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
        var samples = new float[5000];
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
}
