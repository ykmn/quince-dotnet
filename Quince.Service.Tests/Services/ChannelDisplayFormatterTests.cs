using Quince.Service.Configuration;
using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class ChannelDisplayFormatterTests
{
    [Theory]
    [InlineData("icecast")]
    [InlineData("icecast_mp3")]
    public void DetectStreamTypeMismatch_M3u8UrlWithNonHlsType_ReturnsUrlLooksHlsButTypeIsNot(string streamType)
    {
        var source = new SourceConfig { Url = "https://example.com/stream/playlist.m3u8", StreamType = streamType };

        Assert.Equal(StreamTypeMismatch.UrlLooksHlsButTypeIsNot, ChannelDisplayFormatter.DetectStreamTypeMismatch(source));
    }

    [Fact]
    public void DetectStreamTypeMismatch_M3u8UrlWithHlsType_ReturnsNone()
    {
        var source = new SourceConfig { Url = "https://example.com/stream/playlist.m3u8", StreamType = "hls" };

        Assert.Equal(StreamTypeMismatch.None, ChannelDisplayFormatter.DetectStreamTypeMismatch(source));
    }

    [Fact]
    public void DetectStreamTypeMismatch_PlainUrlWithHlsType_ReturnsTypeIsHlsButUrlDoesNot()
    {
        var source = new SourceConfig { Url = "https://example.com/stream.mp3", StreamType = "hls" };

        Assert.Equal(StreamTypeMismatch.TypeIsHlsButUrlDoesNot, ChannelDisplayFormatter.DetectStreamTypeMismatch(source));
    }

    [Fact]
    public void DetectStreamTypeMismatch_PlainUrlWithIcecastType_ReturnsNone()
    {
        var source = new SourceConfig { Url = "https://example.com/stream.mp3", StreamType = "icecast" };

        Assert.Equal(StreamTypeMismatch.None, ChannelDisplayFormatter.DetectStreamTypeMismatch(source));
    }

    [Fact]
    public void DetectStreamTypeMismatch_EmptyUrl_ReturnsNoneRegardlessOfType()
    {
        var source = new SourceConfig { Url = "", StreamType = "hls" };

        Assert.Equal(StreamTypeMismatch.None, ChannelDisplayFormatter.DetectStreamTypeMismatch(source));
    }
}
