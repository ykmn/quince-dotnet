using System.Globalization;

namespace Quince.Service.Audio;

/// <summary>
/// Parses the small parts of an HLS (m3u8) playlist needed to measure a channel's real segment
/// cadence for <see cref="HlsSegmentDurationService"/> — not a general-purpose m3u8 library, just
/// the two tags that matter here: whether this is a master playlist (rendition variants, no
/// segments of its own) versus a media playlist (has <c>#EXT-X-TARGETDURATION</c> directly), and
/// extracting that value. Same "small hand-rolled parser for one known real-world text format" shape
/// as <see cref="Livewire.LwrpParser"/>/<see cref="HlsMetadataReader"/>'s own JSON extraction.
/// </summary>
internal static class HlsPlaylistParser
{
    private const string StreamInfTag = "#EXT-X-STREAM-INF";
    private const string TargetDurationTag = "#EXT-X-TARGETDURATION:";

    /// <summary>True if this is a master playlist (lists rendition variants via
    /// <c>#EXT-X-STREAM-INF</c>) rather than a media playlist (segments directly, has its own
    /// <c>#EXT-X-TARGETDURATION</c>). HLS playlists are one or the other, never both.</summary>
    internal static bool IsMasterPlaylist(string playlistText) =>
        playlistText.Split('\n').Any(l => l.TrimStart().StartsWith(StreamInfTag, StringComparison.Ordinal));

    /// <summary>Returns every variant URI listed after an <c>#EXT-X-STREAM-INF</c> line, in file
    /// order, resolved against <paramref name="baseUri"/> (handles both a relative path and an
    /// already-absolute URI transparently). Empty if there are none / the text isn't a master
    /// playlist.</summary>
    internal static IReadOnlyList<Uri> ParseVariantUris(string playlistText, Uri baseUri)
    {
        var result = new List<Uri>();
        var lines = playlistText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith(StreamInfTag, StringComparison.Ordinal)) continue;

            // The variant URI is the next non-empty, non-comment line after #EXT-X-STREAM-INF.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0) continue;
                if (candidate.StartsWith("#", StringComparison.Ordinal)) break; // another tag, no URI for this variant
                if (Uri.TryCreate(baseUri, candidate, out var resolved))
                    result.Add(resolved);
                break;
            }
        }
        return result;
    }

    /// <summary>Parses <c>#EXT-X-TARGETDURATION:&lt;seconds&gt;</c> (the nominal upper bound on this
    /// playlist's segment duration — the value whose periodic wait, once per segment, causes the
    /// visible gap this class exists to measure). Returns null if the tag is absent, the value isn't
    /// a valid number, or is zero/negative — never a bogus non-positive delay.</summary>
    internal static double? ParseTargetDurationSeconds(string playlistText)
    {
        foreach (var rawLine in playlistText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(TargetDurationTag, StringComparison.Ordinal)) continue;

            var value = line[TargetDurationTag.Length..].Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                return seconds;
            return null;
        }
        return null;
    }
}
