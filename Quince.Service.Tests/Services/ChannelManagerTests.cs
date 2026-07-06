using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class ChannelManagerTests
{
    [Fact]
    public void GenerateFilename_NoCollision_UsesPlainName()
    {
        var result = ChannelManager.GenerateFilename("Retro FM", existingFilenames: Array.Empty<string>());
        Assert.Equal("Retro FM.yaml", result);
    }

    [Fact]
    public void GenerateFilename_Collision_AppendsIncrementingSuffix()
    {
        var result = ChannelManager.GenerateFilename("Retro FM", new[] { "Retro FM.yaml", "Retro FM (2).yaml" });
        Assert.Equal("Retro FM (3).yaml", result);
    }

    [Fact]
    public void GenerateFilename_CollisionIsCaseInsensitive()
    {
        var result = ChannelManager.GenerateFilename("Retro FM", new[] { "retro fm.yaml" });
        Assert.Equal("Retro FM (2).yaml", result);
    }

    [Fact]
    public void GenerateFilename_InvalidPathChars_ReplacedWithUnderscore()
    {
        var result = ChannelManager.GenerateFilename("A/B:C*D", existingFilenames: Array.Empty<string>());
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
    }

    [Fact]
    public void GenerateFilename_BlankName_FallsBackToChannel()
    {
        var result = ChannelManager.GenerateFilename("   ", existingFilenames: Array.Empty<string>());
        Assert.Equal("channel.yaml", result);
    }

    [Fact]
    public void MakeUniqueName_NoCollision_ReturnsBaseName()
    {
        var result = ChannelManager.MakeUniqueName("Retro FM (копия)", existingNames: Array.Empty<string>());
        Assert.Equal("Retro FM (копия)", result);
    }

    [Fact]
    public void MakeUniqueName_Collision_AppendsIncrementingNumber()
    {
        var result = ChannelManager.MakeUniqueName("Retro FM (копия)", new[] { "Retro FM (копия)" });
        Assert.Equal("Retro FM (копия) 2", result);
    }

    [Fact]
    public void MakeUniqueName_MultipleCollisions_SkipsTakenNumbers()
    {
        var result = ChannelManager.MakeUniqueName("Retro FM (копия)", new[] { "Retro FM (копия)", "Retro FM (копия) 2", "Retro FM (копия) 3" });
        Assert.Equal("Retro FM (копия) 4", result);
    }
}
