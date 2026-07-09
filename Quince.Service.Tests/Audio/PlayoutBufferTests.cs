using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using System.Threading.Channels;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class PlayoutBufferTests
{
    private const int SampleRate = 44100;

    // 100ms worth of mono frames — same order of magnitude as StreamCapture's real ~93ms blocks.
    private static AudioChunk MakeChunk(double seconds = 0.1) =>
        new(new float[(int)(seconds * SampleRate)], channels: 1);

    [Fact]
    public void ReleaseDue_BeforePriming_ReleasesNothing()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 1.0);

        buffer.Enqueue(MakeChunk(0.1));
        buffer.ReleaseDue();

        Assert.False(buffer.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReleaseDue_AfterPriming_ReleasesChunksPacedToRealTime()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        // Small target delay so the test doesn't need to wait the real 12s production default.
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.3);

        // Prime with 300ms of audio (3 chunks of 100ms) — crosses the target delay, starting the
        // release clock.
        buffer.Enqueue(MakeChunk(0.1));
        buffer.Enqueue(MakeChunk(0.1));
        buffer.Enqueue(MakeChunk(0.1));

        // Immediately after priming, nothing should be due yet (elapsed since the release anchor is
        // ~0).
        buffer.ReleaseDue();
        Assert.False(buffer.Reader.TryRead(out _));

        // Only real Task.Delay in this test, mirroring the existing decay-timer test's approach
        // (LevelMeterTests.DecayTick_AfterGraceWindowElapsed...) — wait past one chunk's duration and
        // confirm it gets released.
        await Task.Delay(150);
        buffer.ReleaseDue();

        Assert.True(buffer.Reader.TryRead(out _));
    }

    [Fact]
    public void ReleaseDue_QueueDrainedDry_DoesNotThrow()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.1);

        buffer.Enqueue(MakeChunk(0.1));

        // Simulate an outage far longer than the buffered depth: repeated release ticks with nothing
        // left to give should just no-op, never throw.
        for (var i = 0; i < 5; i++) buffer.ReleaseDue();

        Assert.True(true);
    }

    [Fact]
    public void Enqueue_AccumulatesTowardTargetDelay_PrimesExactlyOnceThresholdCrossed()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.25);

        buffer.Enqueue(MakeChunk(0.1));
        buffer.ReleaseDue();
        Assert.False(buffer.Reader.TryRead(out _), "Not primed yet at 100ms buffered (< 250ms target).");

        buffer.Enqueue(MakeChunk(0.1));
        buffer.ReleaseDue();
        Assert.False(buffer.Reader.TryRead(out _), "Not primed yet at 200ms buffered (< 250ms target).");

        buffer.Enqueue(MakeChunk(0.1));
        // Now primed at 300ms buffered (>= 250ms target); release clock just started, so still
        // nothing due on this same tick.
        buffer.ReleaseDue();
        Assert.False(buffer.Reader.TryRead(out _), "Priming just completed — release clock hasn't advanced yet.");
    }
}
