using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>
/// Result of a one-shot "does this stream have metadata" probe, as used by the "Определить
/// наличие метаданных" button in the channel edit dialog. <see cref="MetadataUrl"/> is exactly
/// the value that gets stored in <see cref="SourceConfig.MetadataUrl"/>: "icy" for ICY inline
/// metadata, a discovered JSON endpoint URL, or "" if nothing was found.
/// </summary>
public readonly record struct MetadataDetectionResult(bool Found, string MetadataUrl, string? Warning);

/// <summary>
/// Backs the channel edit dialog's metadata detection button. Mirrors the legacy Python port's
/// <c>ChannelDialog._on_detect_metadata</c> exactly: HLS streams get JSON-endpoint auto-discovery,
/// everything else gets an ICY inline-metadata probe.
/// </summary>
public sealed class MetadataDetectionService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<MetadataDetectionResult> DetectAsync(SourceConfig source)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return new MetadataDetectionResult(false, "", "Введите URL потока");

        if (source.StreamType == "hls")
        {
            var url = await HlsMetadataReader.DiscoverMetadataUrlAsync(source.Url, source.AllowInvalidSsl, Timeout);
            return new MetadataDetectionResult(url != null, url ?? "", null);
        }

        var found = await IcecastMetadataReader.ProbeAsync(source.Url, source.AllowInvalidSsl, Timeout);
        return new MetadataDetectionResult(found, found ? "icy" : "", null);
    }
}
