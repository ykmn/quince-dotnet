using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class StreamCaptureTests
{
    [Fact]
    public void BuildFfmpegArgs_IcecastUrl_DoesNotAddHlsFlags()
    {
        var args = StreamCapture.BuildFfmpegArgs("http://example.com/stream", "icecast",
            allowInvalidSsl: false, hlsBitrateIndex: 0, userAgent: "TestAgent/1.0");

        Assert.DoesNotContain("-allowed_extensions", args);
        Assert.DoesNotContain("-map", args);
        Assert.Contains("-i", args);
        Assert.Contains("http://example.com/stream", args);
        Assert.Contains("pcm_f32le", args);
    }

    [Fact]
    public void BuildFfmpegArgs_Icecast_AddsNobuffer()
    {
        var args = StreamCapture.BuildFfmpegArgs("http://example.com/stream", "icecast",
            allowInvalidSsl: false, hlsBitrateIndex: 0, userAgent: "TestAgent/1.0");

        Assert.Contains("-fflags", args);
        Assert.Contains("nobuffer", args);
    }

    [Fact]
    public void BuildFfmpegArgs_Hls_AddsAllowedExtensionsAndMap()
    {
        var args = StreamCapture.BuildFfmpegArgs("https://example.com/playlist.m3u8", "hls",
            allowInvalidSsl: false, hlsBitrateIndex: 2, userAgent: "TestAgent/1.0");

        Assert.Contains("-allowed_extensions", args);
        Assert.Contains("ALL", args);
        Assert.Contains("-map", args);
        Assert.Contains("0:a:2", args);
        Assert.Contains("-live_start_index", args);
        Assert.Contains("-1", args);
    }

    [Fact]
    public void BuildFfmpegArgs_Hls_DoesNotAddNobuffer()
    {
        var args = StreamCapture.BuildFfmpegArgs("https://example.com/playlist.m3u8", "hls",
            allowInvalidSsl: false, hlsBitrateIndex: 2, userAgent: "TestAgent/1.0");

        Assert.DoesNotContain("nobuffer", args);
    }

    [Fact]
    public void BuildFfmpegArgs_Icecast_DoesNotAddLiveStartIndex()
    {
        var args = StreamCapture.BuildFfmpegArgs("http://example.com/stream", "icecast",
            allowInvalidSsl: false, hlsBitrateIndex: 0, userAgent: "TestAgent/1.0");

        Assert.DoesNotContain("-live_start_index", args);
    }

    [Fact]
    public void BuildFfmpegArgs_AllowInvalidSsl_AddsTlsVerifyOff()
    {
        var args = StreamCapture.BuildFfmpegArgs("https://example.com/stream", "icecast",
            allowInvalidSsl: true, hlsBitrateIndex: 0, userAgent: "TestAgent/1.0");

        Assert.Contains("-tls_verify", args);
        Assert.Contains("0", args);
    }
}
