using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class BackfillBufferTests
{
    private static AudioChunk MakeChunk(float value, int frames) => new(Enumerable.Repeat(value, frames).ToArray(), channels: 1);

    // docs/HISTORY.md #142: resume_seconds = 1, sound physically returns at 01:02:05, the silence
    // detector confirms recovery at 01:02:06 (its own resume_seconds debounce) — recording must
    // backfill from 01:02:04, i.e. 2 * resume_seconds before the confirmed instant. A BackfillBuffer
    // sized to 2 * resume_seconds and fed every chunk seen while paused reproduces exactly that: by
    // the time "confirmed" fires, it holds only the trailing 2 * resume_seconds of the paused feed.
    [Fact]
    public void DrainAll_KeepsOnlyTrailingWindow_TrimmingOldestFirst()
    {
        const int sampleRate = 1000;
        const int framesPerChunk = 100; // 0.1s per chunk at 1000 Hz
        var buffer = new BackfillBuffer(windowSeconds: 0.6, sampleRate); // 2 * resume_seconds(0.3)

        // 10 chunks of 0.1s = 1.0s of paused audio, values 1..10 in arrival order.
        for (var i = 1; i <= 10; i++)
            buffer.Enqueue(MakeChunk(i, framesPerChunk));

        var drained = buffer.DrainAll();

        // Only the last 0.6s (6 chunks) survive: values 5..10, oldest four (1..4) trimmed.
        Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10 }, drained.Select(c => c.Samples[0]).ToArray());
    }

    [Fact]
    public void DrainAll_EmptiesTheBuffer()
    {
        var buffer = new BackfillBuffer(windowSeconds: 1.0, sampleRate: 1000);
        buffer.Enqueue(MakeChunk(1, 100));

        buffer.DrainAll();
        var second = buffer.DrainAll();

        Assert.Empty(second);
    }

    [Fact]
    public void Enqueue_ZeroWindow_NeverBuffersAnything()
    {
        var buffer = new BackfillBuffer(windowSeconds: 0, sampleRate: 1000);
        buffer.Enqueue(MakeChunk(1, 100));

        Assert.Empty(buffer.DrainAll());
    }

    [Fact]
    public void DrainAll_WindowNotYetFull_ReturnsEverythingBuffered()
    {
        var buffer = new BackfillBuffer(windowSeconds: 1.0, sampleRate: 1000);
        buffer.Enqueue(MakeChunk(1, 100));
        buffer.Enqueue(MakeChunk(2, 100));

        var drained = buffer.DrainAll();

        Assert.Equal(new float[] { 1, 2 }, drained.Select(c => c.Samples[0]).ToArray());
    }
}
