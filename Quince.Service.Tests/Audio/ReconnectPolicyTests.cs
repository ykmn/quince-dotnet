using System.Net;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class ReconnectPolicyTests
{
    [Theory]
    [InlineData(3, 1, 3)]
    [InlineData(3, 2, 6)]
    [InlineData(3, 3, 12)]
    [InlineData(3, 4, 24)]
    [InlineData(3, 5, 48)]
    [InlineData(3, 6, 60)]
    [InlineData(3, 6666, 60)]
    [InlineData(0, 1, 1)]
    [InlineData(120, 1, 120)]
    [InlineData(120, 5, 120)]
    public void DelaySeconds_DoublesUpToCap(int baseDelay, int attempt, int expected) =>
        Assert.Equal(expected, ReconnectPolicy.DelaySeconds(baseDelay, attempt));

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(25, false)]
    [InlineData(6660, true)]
    public void ShouldLogAttempt_FirstThreeThenEveryTenth(int attempt, bool expected) =>
        Assert.Equal(expected, ReconnectPolicy.ShouldLogAttempt(attempt));

    [Fact]
    public void NextPollDelay_BacksOffAndNormalizes()
    {
        var poll = HlsMetadataReader.PollInterval;
        Assert.Equal(poll, HlsMetadataReader.NextPollDelay(0));
        Assert.Equal(poll, HlsMetadataReader.NextPollDelay(2));
        Assert.Equal(poll * 6, HlsMetadataReader.NextPollDelay(3));
        Assert.Equal(poll * 6, HlsMetadataReader.NextPollDelay(9));
        Assert.Equal(poll * 12, HlsMetadataReader.NextPollDelay(10));
    }

    [Fact]
    public void DescribeFailure_HttpStatusAndTimeout_AreOneLine()
    {
        Assert.Equal("HTTP 404", HlsMetadataReader.DescribeFailure(
            new HttpRequestException("x", null, HttpStatusCode.NotFound)));
        Assert.Equal("HTTP 503", HlsMetadataReader.DescribeFailure(
            new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("таймаут запроса", HlsMetadataReader.DescribeFailure(new TaskCanceledException()));
    }
}
