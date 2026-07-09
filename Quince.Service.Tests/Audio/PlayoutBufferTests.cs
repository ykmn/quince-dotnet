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

    // Same as MakeChunk but stamps an identifying marker in the first sample, so a test can tell
    // which of several released chunks survived (used for the drop-oldest test below).
    private static AudioChunk MakeMarkedChunk(double seconds, float marker)
    {
        var chunk = MakeChunk(seconds);
        chunk.Samples[0] = marker;
        return chunk;
    }

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

    [Fact]
    public async Task ReleaseDue_ConsumerFallsBehind_DropsOldestKeepsNewestWithinCapacity()
    {
        // Regression test for docs/HISTORY.md #64: an unbounded output channel let a single slow
        // consumer accumulate an ever-growing, never-trimmed backlog (observed in the field as
        // browser listen-in audio drifting tens of seconds behind the meter). The output channel
        // must instead drop the oldest not-yet-consumed chunks once its bounded capacity is
        // exceeded, so the stream self-corrects back toward the target lag.
        const double chunkSeconds = 0.01; // tiny chunks so a short real Task.Delay covers many of them
        const int totalChunks = 50; // more than OutputCapacity (30) so some must be dropped
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.05);

        for (var i = 0; i < totalChunks; i++)
            buffer.Enqueue(MakeMarkedChunk(chunkSeconds, marker: i));

        // Simulate a consumer that never reads from Reader while all this content becomes due —
        // wait past the total content duration (50 * 10ms = 500ms) so a single ReleaseDue() call
        // tries to hand off every remaining queued chunk to the (bounded) output channel at once.
        await Task.Delay(600);
        buffer.ReleaseDue();

        var received = new List<float>();
        while (buffer.Reader.TryRead(out var chunk)) received.Add(chunk.Samples[0]);

        Assert.True(received.Count <= 30, $"Expected at most the bounded capacity (30), got {received.Count}.");
        Assert.NotEmpty(received);
        // The oldest markers (0..19) must have been dropped; the newest (up to 49) must survive, in order.
        Assert.Equal(totalChunks - received.Count, (int)received[0]);
        Assert.Equal(totalChunks - 1, (int)received[^1]);
        Assert.Equal(received.Order(), received);
    }
}
