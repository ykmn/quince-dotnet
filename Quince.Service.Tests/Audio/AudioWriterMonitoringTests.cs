using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Quince.Service.Configuration;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class AudioWriterMonitoringTests
{
    [Fact]
    public async Task RecordingBecomesUnhealthyWhenEncoderExits_WithoutWaitingForAnotherAudioChunk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quince-writer-monitor-" + Guid.NewGuid());
        var input = Channel.CreateUnbounded<AudioChunk>();
        var writer = new AudioWriter(new ChannelConfig
        {
            SavePath = dir, RetentionDays = 0,
            OutputFormat = new() { Mode = "custom", FileFormat = "wav" }
        }, input.Reader, 44100, 2, Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"), NullLogger.Instance);
        try
        {
            writer.Start();
            Assert.False(writer.IsWriting);
            await input.Writer.WriteAsync(new AudioChunk(new float[88200], 2));
            for (var i = 0; i < 300 && !writer.IsWriting; i++) await Task.Delay(10);
            Assert.True(writer.IsWriting);
            using var process = Process.GetProcessById(writer.ProcessId!.Value);
            process.Kill();
            await process.WaitForExitAsync();
            Assert.False(writer.IsWriting);
        }
        finally
        {
            input.Writer.TryComplete();
            writer.Stop();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
