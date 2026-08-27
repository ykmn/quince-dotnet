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
        var reader = buffer.Subscribe("test");

        buffer.Enqueue(MakeChunk(0.1));
        buffer.ReleaseDue();

        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public async Task ReleaseDue_AfterPriming_ReleasesChunksPacedToRealTime()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        // Small target delay so the test doesn't need to wait the real 12s production default.
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.3);
        var reader = buffer.Subscribe("test");

        // Prime with 300ms of audio (3 chunks of 100ms) — crosses the target delay, starting the
        // release clock.
        buffer.Enqueue(MakeChunk(0.1));
        buffer.Enqueue(MakeChunk(0.1));
        buffer.Enqueue(MakeChunk(0.1));

        // Immediately after priming, nothing should be due yet (elapsed since the release anchor is
        // ~0).
        buffer.ReleaseDue();
        Assert.False(reader.TryRead(out _));

        // Only real Task.Delay in this test, mirroring the existing decay-timer test's approach
        // (LevelMeterTests.DecayTick_AfterGraceWindowElapsed...) — wait past one chunk's duration and
        // confirm it gets released.
        await Task.Delay(150);
        buffer.ReleaseDue();

        Assert.True(reader.TryRead(out _));
    }

    [Fact]
    public void ReleaseDue_QueueDrainedDry_DoesNotThrow()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.1);
        buffer.Subscribe("test");

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
        var reader = buffer.Subscribe("test");

        buffer.Enqueue(MakeChunk(0.1));
        buffer.ReleaseDue();
        Assert.False(reader.TryRead(out _), "Not primed yet at 100ms buffered (< 250ms target).");

        buffer.Enqueue(MakeChunk(0.1));
        buffer.ReleaseDue();
        Assert.False(reader.TryRead(out _), "Not primed yet at 200ms buffered (< 250ms target).");

        buffer.Enqueue(MakeChunk(0.1));
        // Now primed at 300ms buffered (>= 250ms target); release clock just started, so still
        // nothing due on this same tick.
        buffer.ReleaseDue();
        Assert.False(reader.TryRead(out _), "Priming just completed — release clock hasn't advanced yet.");
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
        var reader = buffer.Subscribe("test");

        for (var i = 0; i < totalChunks; i++)
            buffer.Enqueue(MakeMarkedChunk(chunkSeconds, marker: i));

        // Simulate a consumer that never reads from Reader while all this content becomes due —
        // wait past the total content duration (50 * 10ms = 500ms) so a single ReleaseDue() call
        // tries to hand off every remaining queued chunk to the (bounded) output channel at once.
        await Task.Delay(600);
        buffer.ReleaseDue();

        var received = new List<float>();
        while (reader.TryRead(out var chunk)) received.Add(chunk.Samples[0]);

        Assert.True(received.Count <= 30, $"Expected at most the bounded capacity (30), got {received.Count}.");
        Assert.NotEmpty(received);
        // The oldest markers (0..19) must have been dropped; the newest (up to 49) must survive, in order.
        Assert.Equal(totalChunks - received.Count, (int)received[0]);
        Assert.Equal(totalChunks - 1, (int)received[^1]);
        Assert.Equal(received.Order(), received);
    }

    [Fact]
    public async Task Subscribe_TwoConsumers_BothReceiveTheSameReleasedChunks()
    {
        // The whole point of this change: a listen-in subscriber and the meter must be able to share
        // one already-primed PlayoutBuffer instead of each needing their own.
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.1);
        var meterReader = buffer.Subscribe("meter");
        var listenInReader = buffer.Subscribe("listen-in");

        buffer.Enqueue(MakeChunk(0.1)); // crosses the 0.1s target — primes immediately
        await Task.Delay(150);
        buffer.ReleaseDue();

        Assert.True(meterReader.TryRead(out var meterChunk));
        Assert.True(listenInReader.TryRead(out var listenInChunk));
        // AudioChunk is a readonly struct wrapping a float[] — both subscribers must have been handed
        // the exact same underlying chunk (same array instance), not independently-decoded copies.
        Assert.True(ReferenceEquals(meterChunk.Samples, listenInChunk.Samples));
    }

    [Fact]
    public async Task Subscribe_LateJoiner_DoesNotReceiveChunksAlreadyReleasedToEarlierSubscribers()
    {
        // No backfill for a subscriber that joins after the buffer already primed and started
        // releasing — same semantics FfmpegPipedCapture's raw fan-out already has for new subscribers.
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.1);
        buffer.Subscribe("meter");

        buffer.Enqueue(MakeChunk(0.1));
        await Task.Delay(150);
        buffer.ReleaseDue(); // released to "meter" only — "listen-in" doesn't exist yet

        var lateReader = buffer.Subscribe("listen-in");
        Assert.False(lateReader.TryRead(out _));
    }

    [Fact]
    public async Task Unsubscribe_StopsThatConsumerWithoutAffectingOthers()
    {
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.1);
        var meterReader = buffer.Subscribe("meter");
        var listenInReader = buffer.Subscribe("listen-in");

        buffer.Enqueue(MakeChunk(0.1));
        await Task.Delay(150);
        buffer.ReleaseDue();
        Assert.True(meterReader.TryRead(out _));
        Assert.True(listenInReader.TryRead(out _));

        buffer.Unsubscribe("listen-in");

        buffer.Enqueue(MakeChunk(0.1));
        await Task.Delay(150);
        buffer.ReleaseDue();

        Assert.True(meterReader.TryRead(out _), "Remaining subscriber must keep receiving chunks.");
        Assert.False(listenInReader.TryRead(out _), "Unsubscribed consumer must not receive further chunks.");
    }

    [Fact]
    public async Task Stop_CompletesEveryAttachedSubscriberReader()
    {
        // So a listen-in HTTP request's ReadAllAsync loop ends cleanly (stream just ends) instead of
        // hanging forever when the owning channel stops or restarts.
        var source = Channel.CreateUnbounded<AudioChunk>();
        var buffer = new PlayoutBuffer(source.Reader, SampleRate, NullLogger.Instance, targetDelaySeconds: 0.05);
        var reader = buffer.Subscribe("listen-in");
        buffer.Start();

        await source.Writer.WriteAsync(MakeChunk(0.1));

        buffer.Stop();

        var completed = await Task.WhenAny(reader.Completion, Task.Delay(TimeSpan.FromSeconds(2))) == reader.Completion;
        Assert.True(completed, "Subscriber reader must complete once the buffer stops.");
    }
}
