using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class IcecastMetadataReaderTests
{
    [Fact]
    public void ParseMetadataString_ArtistTitle_Splits()
    {
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("George Michael - Freedom");
        Assert.Equal("George Michael", artist);
        Assert.Equal("Freedom", title);
    }

    [Fact]
    public void ParseMetadataString_NoDash_WholeStringIsTitle()
    {
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("Frozen");
        Assert.Equal("", artist);
        Assert.Equal("Frozen", title);
    }

    [Fact]
    public void ParseMetadataString_StripsWhitespace()
    {
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("  Artist  -  Title  ");
        Assert.Equal("Artist", artist);
        Assert.Equal("Title", title);
    }

    [Fact]
    public void ParseMetadataString_EmptyString_ReturnsEmptyBoth()
    {
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("");
        Assert.Equal("", artist);
        Assert.Equal("", title);
    }

    [Fact]
    public void ParseMetadataString_MultipleDashes_SplitsOnFirst()
    {
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("a - b - c");
        Assert.Equal("a", artist);
        Assert.Equal("b - c", title);
    }
}
