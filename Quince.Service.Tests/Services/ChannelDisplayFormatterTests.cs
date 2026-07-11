using Quince.Service.Audio;
using Quince.Service.Configuration;
using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class ChannelDisplayFormatterTests
{
    [Fact]
    public void ClassifyRunState_IsRecording_ReturnsRunning()
    {
        var status = new EngineStatus(IsRecording: true);

        Assert.Equal(ChannelRunState.Running, ChannelDisplayFormatter.ClassifyRunState(status));
    }

    [Fact]
    public void ClassifyRunState_IsRecordingAndHasError_ReturnsRunning()
    {
        // Shouldn't be reachable in practice (HasError is only set once IsRecording goes false), but
        // IsRecording must still win if it somehow is — matches ChannelCard's own dot logic exactly.
        var status = new EngineStatus(IsRecording: true, HasError: true);

        Assert.Equal(ChannelRunState.Running, ChannelDisplayFormatter.ClassifyRunState(status));
    }

    [Fact]
    public void ClassifyRunState_HasErrorNotRecording_ReturnsError()
    {
        var status = new EngineStatus(IsRecording: false, HasError: true);

        Assert.Equal(ChannelRunState.Error, ChannelDisplayFormatter.ClassifyRunState(status));
    }

    [Fact]
    public void ClassifyRunState_NeitherRecordingNorError_ReturnsStopped()
    {
        var status = new EngineStatus(IsRecording: false, HasError: false);

        Assert.Equal(ChannelRunState.Stopped, ChannelDisplayFormatter.ClassifyRunState(status));
    }

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
