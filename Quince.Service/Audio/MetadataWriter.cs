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
    private readonly object _lock = new();
    private PendingEntry? _pending;

    public MetadataWriter(string savePath, string metadataPath)
    {
        _savePath = string.IsNullOrEmpty(metadataPath) ? Path.Combine(savePath, "meta") : metadataPath;
    }

    /// <summary>Called when new metadata arrives. Thread-safe.</summary>
    public void OnMetadata(MetadataEvent evt)
    {
        lock (_lock)
        {
            if (_pending is { } pending)
            {
                var duration = evt.Timestamp - pending.Timestamp;
                WriteRow(pending.Timestamp, pending.Title, pending.Artist, ElemClass(pending.Title), FormatDuration(duration));
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
            WriteRow(pending.Timestamp, pending.Title, pending.Artist, ElemClass(pending.Title), FormatDuration(duration));
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
            WriteRow(pending.Timestamp, pending.Title, pending.Artist, ElemClass(pending.Title), "");
            _pending = null;
        }
    }

    private static string ElemClass(string title) => string.IsNullOrEmpty(title) ? "B" : "M";

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

        writer.WriteLine(CsvRow(dt.ToString("yyyy-MM-dd HH:mm"), name, artist, elemClass, lengthStr));
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
