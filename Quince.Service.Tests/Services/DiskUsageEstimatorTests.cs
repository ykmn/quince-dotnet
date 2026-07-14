using Quince.Service.Configuration;
using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class DiskUsageEstimatorTests
{
    private static ChannelConfig CustomMp3(int bitrateKbps, int retentionDays) => new()
    {
        Source = new SourceConfig { Type = "stream", StreamType = "icecast" },
        OutputFormat = new OutputFormatConfig { Mode = "custom", FileFormat = "mp3", BitrateKbps = bitrateKbps },
        RetentionDays = retentionDays,
    };

    [Fact]
    public void EstimateBytesPerSecond_Mp3_UsesConfiguredBitrate()
    {
        var config = CustomMp3(bitrateKbps: 128, retentionDays: 30);

        // 128 kbps = 128000 bits/s / 8 = 16000 bytes/s.
        Assert.Equal(16_000, DiskUsageEstimator.EstimateBytesPerSecond(config));
    }

    [Fact]
    public void EstimateBytesPerSecond_Wav_UsesSampleRateChannelsBitDepth()
    {
        var config = new ChannelConfig
        {
            Source = new SourceConfig { Type = "soundcard" },
            OutputFormat = new OutputFormatConfig { Mode = "custom", FileFormat = "wav", SampleRate = 44100, BitDepth = 16, Channels = 2 },
        };

        // 44100 * 2 bytes/sample (16-bit) * 2 channels.
        Assert.Equal(44_100L * 2 * 2, DiskUsageEstimator.EstimateBytesPerSecond(config));
    }

    [Fact]
    public void EstimateBytesPerSecond_OriginalMode_ResolvesFormatFromSource()
    {
        // HLS source in "original" mode resolves to AAC (see AudioWriter.ResolveEffectiveFormat) —
        // the estimate should follow that resolved format's bitrate, not treat it as WAV.
        var config = new ChannelConfig
        {
            Source = new SourceConfig { Type = "stream", StreamType = "hls" },
            OutputFormat = new OutputFormatConfig { Mode = "original", BitrateKbps = 96 },
        };

        Assert.Equal(96_000L / 8, DiskUsageEstimator.EstimateBytesPerSecond(config));
    }

    [Fact]
    public void EstimateTotalBytes_MultipliesByRetentionDays()
    {
        var config = CustomMp3(bitrateKbps: 96, retentionDays: 10);
        var bytesPerSecond = DiskUsageEstimator.EstimateBytesPerSecond(config);

        Assert.Equal(bytesPerSecond * 10 * 86400L, DiskUsageEstimator.EstimateTotalBytes(config));
    }

    [Fact]
    public void EstimateTotalBytes_IsIndependentOfFileDuration()
    {
        var short5 = CustomMp3(bitrateKbps: 96, retentionDays: 10);
        short5.FileDurationMinutes = 5;
        var long120 = CustomMp3(bitrateKbps: 96, retentionDays: 10);
        long120.FileDurationMinutes = 120;

        Assert.Equal(DiskUsageEstimator.EstimateTotalBytes(short5), DiskUsageEstimator.EstimateTotalBytes(long120));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EstimateTotalBytes_UnlimitedRetention_ReturnsNull(int retentionDays)
    {
        var config = CustomMp3(bitrateKbps: 96, retentionDays: retentionDays);

        Assert.Null(DiskUsageEstimator.EstimateTotalBytes(config));
    }
}
