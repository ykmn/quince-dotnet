using System.Text;

namespace Quince.Service.Audio;

/// <summary>
/// Receives <see cref="MetadataEvent"/>s and writes them to daily CSV files, one row per track.
/// Direct port of the legacy Python implementation's <c>MetadataWriter</c>
/// (<c>src/audio/metadata_writer.py</c>) — same folder layout, same CSV columns
/// (<c>EventTime, ElemName, ElemArtist, ElemClass, ElemLength</c>), same deferred-row strategy: a
/// row is written when the NEXT event arrives, once <c>ElemLength</c> (the previous track's
/// duration) is known. <see cref="Flush"/>/<see cref="OnSilence"/> flush the pending row early.
/// </summary>
public sealed class MetadataWriter
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private readonly string _savePath;
    private readonly Func<IReadOnlyList<string>>? _getAdKeywords;
    private readonly Func<IReadOnlyList<string>>? _getNewsKeywords;
    private readonly object _lock = new();
    private PendingEntry? _pending;

    /// <param name="getAdKeywords">Reads the live-current ad-keyword list at classification time
    /// (not just a snapshot from when the channel started) — null skips "C" classification (ElemClass
    /// is then only ever "B"/"M"/"N").</param>
    /// <param name="getNewsKeywords">Same idea as <paramref name="getAdKeywords"/> but for "N" (news).</param>
    public MetadataWriter(string savePath, string metadataPath, Func<IReadOnlyList<string>>? getAdKeywords = null,
        Func<IReadOnlyList<string>>? getNewsKeywords = null)
    {
        _savePath = string.IsNullOrEmpty(metadataPath) ? Path.Combine(savePath, "meta") : metadataPath;
        _getAdKeywords = getAdKeywords;
        _getNewsKeywords = getNewsKeywords;
    }

    /// <summary>Called when new metadata arrives. Thread-safe.</summary>
    public void OnMetadata(MetadataEvent evt)
    {
        lock (_lock)
        {
            if (_pending is { } pending)
            {
                var duration = evt.Timestamp - pending.Timestamp;
                WriteRow(pending.Timestamp, pending.Title, pending.Artist, ElemClass(pending.Title, pending.Artist), FormatDuration(duration));
            }

            _pending = new PendingEntry(evt.Timestamp, string.IsNullOrEmpty(evt.Title) ? evt.Raw : evt.Title, evt.Artist);
        }
    }

    /// <summary>Called when stream metadata disappears (e.g. an ad break with no title).
    /// Finalises the pending row with a known duration and adds a gap-marker row.</summary>
    public void OnSilence()
    {
        lock (_lock)
        {
            if (_pending is not { } pending) return;

            var now = DateTimeOffset.Now;
            var duration = now - pending.Timestamp;
            WriteRow(pending.Timestamp, pending.Title, pending.Artist, ElemClass(pending.Title, pending.Artist), FormatDuration(duration));
            _pending = null;
            WriteRow(now, "", "", "", "");
        }
    }

    /// <summary>Called on stop. Writes the last pending row without ElemLength.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_pending is not { } pending) return;
            WriteRow(pending.Timestamp, pending.Title, pending.Artist, ElemClass(pending.Title, pending.Artist), "");
            _pending = null;
        }
    }

    /// <summary>"B" for a blank/break marker (no title), "C" if the title/artist contains a
    /// configured ad keyword (case-insensitive substring), "N" if it contains a configured news
    /// keyword, otherwise "M" for ordinary music. Ad keywords are checked first, so a title matching
    /// both lists (unlikely in practice) comes out as "C".</summary>
    private string ElemClass(string title, string artist)
    {
        if (string.IsNullOrEmpty(title)) return "B";

        var haystack = string.IsNullOrEmpty(artist) ? title : $"{artist} {title}";
        if (MatchesAnyKeyword(haystack, _getAdKeywords?.Invoke())) return "C";
        if (MatchesAnyKeyword(haystack, _getNewsKeywords?.Invoke())) return "N";
        return "M";
    }

    private static bool MatchesAnyKeyword(string haystack, IReadOnlyList<string>? keywords)
    {
        if (keywords == null) return false;
        foreach (var keyword in keywords)
        {
            if (!string.IsNullOrEmpty(keyword) && haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void WriteRow(DateTimeOffset dt, string name, string artist, string elemClass, string lengthStr)
    {
        Directory.CreateDirectory(_savePath);
        var csvPath = Path.Combine(_savePath, $"{dt:yyyy-MM-dd}.csv");
        var isNewFile = !File.Exists(csvPath);

        using var stream = new FileStream(csvPath, FileMode.Append, FileAccess.Write);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        if (isNewFile)
        {
            stream.Write(Utf8Bom, 0, Utf8Bom.Length);
            writer.WriteLine(CsvRow("EventTime", "ElemName", "ElemArtist", "ElemClass", "ElemLength"));
        }

        writer.WriteLine(CsvRow(dt.ToString("yyyy-MM-dd HH:mm:ss"), name, artist, elemClass, lengthStr));
    }

    private static string CsvRow(params string[] fields) =>
        string.Join(",", fields.Select(f => "\"" + f.Replace("\"", "\"\"") + "\""));

    /// <summary>Formats a duration as "H:MM:SS.mmm" or "M:SS.mmm", matching the legacy port exactly.</summary>
    internal static string FormatDuration(TimeSpan span)
    {
        var totalSeconds = span.TotalSeconds;
        var ms = (int)(totalSeconds * 1000) % 1000;
        var totalS = (int)totalSeconds;
        var s = totalS % 60;
        var m = (totalS / 60) % 60;
        var h = totalS / 3600;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}.{ms:D3}" : $"{m}:{s:D2}.{ms:D3}";
    }

    private sealed record PendingEntry(DateTimeOffset Timestamp, string Title, string Artist);
}
