using Quince.Service.Configuration;
using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class MetadataDetectionServiceTests
{
    [Fact]
    public async Task DetectAsync_EmptyUrl_ReturnsWarningWithoutNetworkCall()
    {
        var service = new MetadataDetectionService();
        var source = new SourceConfig { Type = "stream", Url = "" };

        var result = await service.DetectAsync(source);

        Assert.False(result.Found);
        Assert.Equal("", result.MetadataUrl);
        Assert.NotNull(result.Warning);
    }
}
