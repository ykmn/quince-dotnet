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

    [Fact]
    public void ParseMetadataString_EmDash_AlsoSplits()
    {
        // Some sources send "Artist — Title" with an em dash instead of a plain hyphen.
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("Dire Straits — Money For Nothing");
        Assert.Equal("Dire Straits", artist);
        Assert.Equal("Money For Nothing", title);
    }

    [Fact]
    public void ParseMetadataString_EmDashWithinTitle_NotMistakenForMissingArtist()
    {
        // An em dash that's part of the title itself (not a separator) must not confuse the split
        // when a real " - " separator is also present — matches production data (Animal ДжаZ —
        // "Москва — Кассиопея", a real track whose own title contains an em dash).
        var (artist, title) = IcecastMetadataReader.ParseMetadataString("Animal ДжаZ - Москва — Кассиопея");
        Assert.Equal("Animal ДжаZ", artist);
        Assert.Equal("Москва — Кассиопея", title);
    }
}
