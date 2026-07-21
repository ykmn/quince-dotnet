using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Quince.Service.Configuration;
using Xunit;

namespace Quince.Service.Tests.Audio;

/// <summary>
/// Covers <c>AudioWriter.CleanupOldFiles()</c>'s expired-date-folder branch — previously untouched by
/// any test (<see cref="AudioWriterPauseResumeTests"/> always starts from an empty temp dir). Focuses
/// on the error-handling hardening: a file that can't be deleted must not escape as an exception, since
/// <c>CleanupOldFiles</c> runs synchronously from <see cref="AudioWriter.Start"/> — before this fix, an
/// <see cref="UnauthorizedAccessException"/> here (only <see cref="IOException"/> was caught) would have
/// propagated out of <c>Start()</c> itself.
/// </summary>
public class AudioWriterCleanupTests
{
    private const int SampleRate = 1000;

    private static string ResolveFfmpegPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        Assert.True(File.Exists(path), $"Test setup problem: ffmpeg.exe not found at {path}");
        return path;
    }

    [Fact]
    public void Start_ReadOnlyFileInExpiredFolder_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "quince-test-" + Guid.NewGuid());
        try
        {
            var expiredFolder = Path.Combine(tempDir, OutputPathPlanner.FormatDate(DateTime.Now.AddDays(-5), "YYYY-MM-DD"));
            Directory.CreateDirectory(expiredFolder);
            var oldFile = Path.Combine(expiredFolder, "00-00-00.wav");
            File.WriteAllBytes(oldFile, new byte[10]);
            // File.Delete on a read-only file throws UnauthorizedAccessException on Windows — the
            // exact case that used to be uncaught (only IOException was handled before this fix).
            File.SetAttributes(oldFile, FileAttributes.ReadOnly);

            var config = new ChannelConfig
            {
                Name = "test",
                SavePath = tempDir,
                RetentionDays = 1, // 5-day-old folder is well past the cutoff
                FileDurationMinutes = 60,
                OutputFormat = new OutputFormatConfig { Mode = "custom", FileFormat = "wav", BitDepth = 16, SampleRate = SampleRate, Channels = 1 },
            };

            var channel = Channel.CreateUnbounded<AudioChunk>();
            var writer = new AudioWriter(config, channel.Reader, SampleRate, inputChannels: 1, ResolveFfmpegPath(), NullLogger.Instance);

            writer.Start(); // must not throw despite the undeletable file
            Assert.True(writer.IsRunning);
            writer.Stop();

            // The read-only file survives — cleanup skipped it rather than crashing.
            Assert.True(File.Exists(oldFile));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
