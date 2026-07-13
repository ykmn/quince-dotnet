using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class HlsPlaylistParserTests
{
    private const string MediaPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:100
        #EXTINF:6.0,
        segment100.ts
        #EXTINF:6.0,
        segment101.ts
        """;

    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-STREAM-INF:BANDWIDTH=128000
        low/playlist.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=256000
        high/playlist.m3u8
        """;

    [Fact]
    public void IsMasterPlaylist_WithStreamInf_ReturnsTrue()
    {
        Assert.True(HlsPlaylistParser.IsMasterPlaylist(MasterPlaylist));
    }

    [Fact]
    public void IsMasterPlaylist_MediaPlaylistOnly_ReturnsFalse()
    {
        Assert.False(HlsPlaylistParser.IsMasterPlaylist(MediaPlaylist));
    }

    [Fact]
    public void ParseTargetDurationSeconds_MediaPlaylist_ReturnsValue()
    {
        Assert.Equal(6.0, HlsPlaylistParser.ParseTargetDurationSeconds(MediaPlaylist));
    }

    [Fact]
    public void ParseTargetDurationSeconds_DecimalValue_Parses()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:6.5\n#EXTINF:6.5,\nsegment0.ts\n";
        Assert.Equal(6.5, HlsPlaylistParser.ParseTargetDurationSeconds(text));
    }

    [Fact]
    public void ParseTargetDurationSeconds_MissingTag_ReturnsNull()
    {
        var text = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6.0,\nsegment0.ts\n";
        Assert.Null(HlsPlaylistParser.ParseTargetDurationSeconds(text));
    }

    [Fact]
    public void ParseTargetDurationSeconds_MalformedValue_ReturnsNull()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:notanumber\n";
        Assert.Null(HlsPlaylistParser.ParseTargetDurationSeconds(text));
    }

    [Fact]
    public void ParseTargetDurationSeconds_Zero_ReturnsNull()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:0\n";
        Assert.Null(HlsPlaylistParser.ParseTargetDurationSeconds(text));
    }

    [Fact]
    public void ParseTargetDurationSeconds_Negative_ReturnsNull()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:-5\n";
        Assert.Null(HlsPlaylistParser.ParseTargetDurationSeconds(text));
    }

    [Fact]
    public void ParseVariantUris_RelativeUris_ResolvedAgainstBaseUri()
    {
        var baseUri = new Uri("https://example.com/live/master.m3u8");
        var variants = HlsPlaylistParser.ParseVariantUris(MasterPlaylist, baseUri);

        Assert.Equal(2, variants.Count);
        Assert.Equal(new Uri("https://example.com/live/low/playlist.m3u8"), variants[0]);
        Assert.Equal(new Uri("https://example.com/live/high/playlist.m3u8"), variants[1]);
    }

    [Fact]
    public void ParseVariantUris_AbsoluteUris_ReturnedAsIs()
    {
        var text = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=128000
            https://cdn.example.com/low/playlist.m3u8
            """;
        var variants = HlsPlaylistParser.ParseVariantUris(text, new Uri("https://example.com/live/master.m3u8"));

        Assert.Single(variants);
        Assert.Equal(new Uri("https://cdn.example.com/low/playlist.m3u8"), variants[0]);
    }

    [Fact]
    public void ParseVariantUris_NoStreamInf_ReturnsEmpty()
    {
        var variants = HlsPlaylistParser.ParseVariantUris(MediaPlaylist, new Uri("https://example.com/live/media.m3u8"));
        Assert.Empty(variants);
    }
}
