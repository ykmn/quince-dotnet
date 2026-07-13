using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class HlsSegmentDurationServiceTests
{
    private static HlsSegmentDurationService MakeService() => new(NullLogger<HlsSegmentDurationService>.Instance);

    [Fact]
    public void TryGetCachedDelaySeconds_NeverRefreshed_ReturnsNull()
    {
        var service = MakeService();
        Assert.Null(service.TryGetCachedDelaySeconds("https://example.com/live/master.m3u8"));
    }

    [Fact]
    public void TryGetCachedDelaySeconds_EmptyUrl_ReturnsNull()
    {
        var service = MakeService();
        Assert.Null(service.TryGetCachedDelaySeconds(""));
    }

    [Fact]
    public async Task RequestRefresh_MalformedUrl_DoesNotThrowAndCacheStaysEmpty()
    {
        // Mirrors HlsMetadataReaderTests.DiscoverMetadataUrlAsync_MalformedPlaylistUrl_ReturnsNullInsteadOfThrowing:
        // RequestRefresh is fire-and-forget, called straight from ChannelEngine.Start() — an unhandled
        // exception here must never escape (it isn't even awaited by the caller), and the cache must
        // simply stay empty rather than get corrupted.
        var service = MakeService();
        const string url = "not a url";

        service.RequestRefresh(url, allowInvalidSsl: false, hlsBitrateIndex: 0, channelName: "test");
        await Task.Delay(500);

        Assert.Null(service.TryGetCachedDelaySeconds(url));
    }

    [Fact]
    public async Task RequestRefresh_RapidRepeatedCallsSameUrl_DoesNotThrow()
    {
        // Dedupe (_inFlight) is an internal optimization, not separately asserted here — this just
        // confirms overlapping calls for the same URL never deadlock or crash, and a call after the
        // first one's already-in-flight window doesn't get stuck forever.
        var service = MakeService();
        const string url = "not a url";

        service.RequestRefresh(url, allowInvalidSsl: false, hlsBitrateIndex: 0, channelName: "test");
        service.RequestRefresh(url, allowInvalidSsl: false, hlsBitrateIndex: 0, channelName: "test");
        await Task.Delay(500);
        service.RequestRefresh(url, allowInvalidSsl: false, hlsBitrateIndex: 0, channelName: "test");
        await Task.Delay(500);

        Assert.Null(service.TryGetCachedDelaySeconds(url));
    }

    [Fact]
    public void ComputeDelaySeconds_TypicalHls_AddsMarginNoClamp()
    {
        Assert.Equal(8.05, HlsSegmentDurationService.ComputeDelaySeconds(6.05), precision: 6);
    }

    [Fact]
    public void ComputeDelaySeconds_TinyDuration_ClampsToFloor()
    {
        Assert.Equal(HlsSegmentDurationService.MinDelaySeconds, HlsSegmentDurationService.ComputeDelaySeconds(0.5));
    }

    [Fact]
    public void ComputeDelaySeconds_HugeDuration_ClampsToCeiling()
    {
        Assert.Equal(HlsSegmentDurationService.MaxDelaySeconds, HlsSegmentDurationService.ComputeDelaySeconds(3600));
    }

    [Fact]
    public void ComputeDelaySeconds_WorstHistoricalCase_MatchesTodaysConstant()
    {
        // Pins the non-regression property: the worst real segment duration ever observed in the
        // field (~10s, docs/HISTORY.md #61) must still land on (or under) today's known-good flat
        // constant (PlayoutBuffer.DefaultTargetDelaySeconds = 12s), not something smaller/riskier.
        Assert.Equal(PlayoutBuffer.DefaultTargetDelaySeconds, HlsSegmentDurationService.ComputeDelaySeconds(10.0));
    }
}
