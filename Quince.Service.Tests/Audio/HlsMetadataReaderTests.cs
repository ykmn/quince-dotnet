using System.Text.Json;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class HlsMetadataReaderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ExtractFromJson_FmgidFormat()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"fmgid": {"artist": "George Michael", "name": "Freedom"}}"""));
        Assert.Equal(("George Michael", "Freedom"), result);
    }

    [Fact]
    public void ExtractFromJson_FmgidUsesSongWhenNameMissing()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"fmgid": {"artist": "Artist", "song": "MySong"}}"""));
        Assert.Equal(("Artist", "MySong"), result);
    }

    [Fact]
    public void ExtractFromJson_FlatArtistTitleFormat()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"artist": "Daft Punk", "title": "Get Lucky"}"""));
        Assert.Equal(("Daft Punk", "Get Lucky"), result);
    }

    [Fact]
    public void ExtractFromJson_FlatSongField()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"song": "Some Song"}"""));
        Assert.Equal(("", "Some Song"), result);
    }

    [Fact]
    public void ExtractFromJson_NowPlayingStringViaParse()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"now_playing": "Artist X - Track Y"}"""));
        Assert.Equal(("Artist X", "Track Y"), result);
    }

    [Fact]
    public void ExtractFromJson_CurrentStringField()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"current": "Solo Title"}"""));
        Assert.Equal(("", "Solo Title"), result);
    }

    [Fact]
    public void ExtractFromJson_EmptyObject_ReturnsNull()
    {
        Assert.Null(HlsMetadataReader.ExtractFromJson(Parse("{}")));
    }

    [Fact]
    public void ExtractFromJson_UnknownKeys_ReturnsNull()
    {
        Assert.Null(HlsMetadataReader.ExtractFromJson(Parse("""{"duration": 200, "bitrate": 128}""")));
    }

    [Fact]
    public void ExtractFromJson_TopLevelWinsOverFmgidPriorityOrder()
    {
        // fmgid checked first per legacy order: if fmgid has a usable title, it wins even
        // though a flat top-level field is also present.
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"fmgid": {"name": "From Fmgid"}, "title": "From Top Level"}"""));
        Assert.Equal(("", "From Fmgid"), result);
    }

    [Fact]
    public void BuildCandidateUrls_DerivesFromPlaylistDirectory()
    {
        var candidates = HlsMetadataReader.BuildCandidateUrls("https://host/11/playlist.m3u8").ToList();
        Assert.Equal(new[]
        {
            "https://host/11/metadata.json?format=fmgid&subformat=small",
            "https://host/11/metadata.json",
            "https://host/metadata.json",
        }, candidates);
    }

    [Fact]
    public async Task DiscoverMetadataUrlAsync_MalformedPlaylistUrl_ReturnsNullInsteadOfThrowing()
    {
        // BuildCandidateUrls parses the URL with `new Uri(...)`, which throws for a bare
        // hostname/relative string — DiscoverMetadataUrlAsync must not let that escape uncaught
        // (it's called from the "Определить наличие метаданных" button and mustn't ever leave
        // the UI stuck on an unhandled exception instead of a plain "not found" result).
        var result = await HlsMetadataReader.DiscoverMetadataUrlAsync("not a url", false, TimeSpan.FromSeconds(1));
        Assert.Null(result);
    }
}
