using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Quince.Service.Configuration;
using Xunit;

namespace Quince.Service.Tests.Audio;

/// <summary>
/// End-to-end (real ffmpeg.exe, real WAV files on disk) verification of docs/HISTORY.md #142/#144:
/// on confirmed silence (<see cref="AudioWriter.SetRecordingActive"/> false) the current file is
/// closed/saved immediately, and on confirmed resume a NEW file is opened that backfills exactly the
/// trailing <c>2 * resume_seconds</c> of audio that arrived while stopped — dropping anything older
/// than that window — before continuing with live chunks. Drives <see cref="AudioWriter"/> directly
/// (bypassing <see cref="SilenceDetector"/>'s own timing) so the exact chunk sequence and backfill
/// boundary are deterministic and byte-verifiable in the resulting files.
/// </summary>
public class AudioWriterPauseResumeTests
{
    private const int SampleRate = 1000; // low rate keeps frame counts small and math exact
    private const int FramesPerChunk = 100; // 0.1s/chunk, matching the ~100ms chunking used elsewhere

    // marker/100 keeps every chunk's amplitude safely within [-1, 1] (markers go up to 99) so ffmpeg's
    // pcm_s16le encoder doesn't clip distinct markers down to the same full-scale value.
    private static AudioChunk MakeChunk(float marker) => new(Enumerable.Repeat(marker / 100f, FramesPerChunk).ToArray(), channels: 1);

    private static string ResolveFfmpegPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        Assert.True(File.Exists(path), $"Test setup problem: ffmpeg.exe not found at {path}");
        return path;
    }

    [Fact]
    public async Task Recording_ClosesFileOnSilence_AndOpensNewBackfilledFileOnResume()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "quince-test-" + Guid.NewGuid());
        try
        {
            var config = new ChannelConfig
            {
                Name = "test",
                SavePath = tempDir,
                FileDurationMinutes = 60,
                OutputFormat = new OutputFormatConfig { Mode = "custom", FileFormat = "wav", BitDepth = 16, SampleRate = SampleRate, Channels = 1 },
                SilenceDetector = new SilenceDetectorConfig { Enabled = true, ResumeSeconds = 0.3 }, // backfill window = 0.6s = 6 chunks
            };

            var channel = Channel.CreateUnbounded<AudioChunk>();
            var writer = new AudioWriter(config, channel.Reader, SampleRate, inputChannels: 1, ResolveFfmpegPath(), NullLogger.Instance);
            writer.Start();

            // Steady-state recording: 3 live chunks written normally (values 1..3).
            for (var i = 1; i <= 3; i++) await channel.Writer.WriteAsync(MakeChunk(i));
            await DrainAndSettleAsync(channel);

            writer.SetRecordingActive(false); // silence confirmed — current file closes/saves now
            // 10 chunks (1.0s) arrive while stopped (values 11..20); only the trailing 0.6s (6 chunks,
            // values 15..20) should survive to be backfilled into the new file once sound is confirmed.
            for (var i = 11; i <= 20; i++) await channel.Writer.WriteAsync(MakeChunk(i));
            await DrainAndSettleAsync(channel);

            // Filenames only have whole-second resolution (hh-mm-ss); without this the first and
            // second file could land in the same wall-clock second (the resume file's name is itself
            // backdated by 2*ResumeSeconds) and silently collide via ffmpeg's -y overwrite, making the
            // test flaky rather than testing the real two-file behavior.
            await Task.Delay(2500);

            writer.SetRecordingActive(true); // sound confirmed back — new file opens + backfills
            await channel.Writer.WriteAsync(MakeChunk(99)); // first live chunk after resume
            await DrainAndSettleAsync(channel);

            writer.Stop();

            var outFiles = Directory.GetFiles(tempDir, "*.wav", SearchOption.AllDirectories).OrderBy(f => f).ToArray();
            Assert.Equal(2, outFiles.Length);

            // First file: only the pre-silence chunks (1,2,3), closed/saved as soon as silence was confirmed.
            var firstSamples = ReadWavInt16Samples(outFiles[0]);
            float[] expectedFirstBlocks = { 1, 2, 3 };
            AssertBlocksMatch(expectedFirstBlocks, firstSamples);

            // Second (new) file: backfilled trailing window (15..20) then the live post-resume chunk (99).
            var secondSamples = ReadWavInt16Samples(outFiles[1]);
            float[] expectedSecondBlocks = { 15, 16, 17, 18, 19, 20, 99 };
            AssertBlocksMatch(expectedSecondBlocks, secondSamples);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void AssertBlocksMatch(float[] expectedBlockValues, short[] samples)
    {
        Assert.Equal(expectedBlockValues.Length * FramesPerChunk, samples.Length);
        for (var block = 0; block < expectedBlockValues.Length; block++)
        {
            var expected = expectedBlockValues[block] / 100f;
            var actual = samples[block * FramesPerChunk] / 32767f;
            Assert.True(Math.Abs(expected - actual) < 0.002f,
                $"Block {block}: expected ~{expected:F4}, got {actual:F4}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    /// <summary>Waits for the writer's background loop to dequeue everything written so far, then adds
    /// a settle delay for the last dequeued chunk's own (near-instant, local-pipe) write to finish —
    /// same real-Task.Delay approach PlayoutBufferTests uses for timing-sensitive async assertions.
    /// Needed so the test's own <see cref="AudioWriter.SetRecordingActive"/> calls land strictly
    /// between the intended batches instead of racing the writer's independent background task.</summary>
    private static async Task DrainAndSettleAsync(Channel<AudioChunk> channel)
    {
        await WaitUntilAsync(() => channel.Reader.Count == 0, TimeSpan.FromSeconds(5));
        await Task.Delay(100);
    }

    private static short[] ReadWavInt16Samples(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(12); // "RIFF" size "WAVE"
        while (stream.Position < stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "data")
            {
                var bytes = reader.ReadBytes(size);
                var samples = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                return samples;
            }
            reader.BaseStream.Seek(size + (size % 2), SeekOrigin.Current);
        }
        throw new InvalidDataException("No 'data' chunk found in WAV file");
    }
}
