using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

public sealed record FolderUsage(long Bytes, DateTimeOffset ScannedAt);

/// <summary>Version 1 monitoring contract. Codes are mapped to labels by the Zabbix template.</summary>
public sealed record ChannelTelemetry(string Id, string Name, string SourceType, int Recording,
    int Metadata, int Audio, int Error, int DiskAvailable, double? ForecastPct, long? UsedBytes,
    long? ForecastBytes, long? DiskScannedAt)
{
    public static ChannelTelemetry Create(ChannelConfig config, EngineStatus? status,
        DateTimeOffset? metadataReceivedAt, LevelReading? level, DateTimeOffset? levelAt,
        FolderUsage? usage, DateTimeOffset now)
    {
        var running = status?.IsRecording == true;
        var paused = config.SilenceDetector.Enabled && !config.SilenceDetector.DontStopRecording
            && status?.IsSilent == true;
        var recording = running && status?.IsFileRecording == true && !paused;
        var metadata = string.IsNullOrWhiteSpace(config.Source.MetadataUrl) ? 0
            : running && metadataReceivedAt is { } received && now - received <= TimeSpan.FromMinutes(5) ? 2 : 1;
        var audio = 0; // Unknown: stopped, priming, or no recent samples.
        if (running && level != null && levelAt is { } measured && now - measured <= TimeSpan.FromSeconds(15))
            audio = config.SilenceDetector.Enabled
                ? status!.IsSilent ? 1 : 2
                : level.TruePeakDb > config.SilenceDetector.ThresholdDbfs ? 2 : 1;
        var total = DiskUsageEstimator.EstimateTotalBytes(config);
        var fresh = usage != null && now - usage.ScannedAt <= TimeSpan.FromMinutes(10);
        var pct = fresh && total > 0
            ? Math.Clamp(usage!.Bytes / (double)total.Value * 100, 0, 100) : (double?)null;
        return new(config.MonitoringId, config.Name, config.Source.Type,
            recording ? 1 : 0, metadata, audio, status?.HasError == true ? 1 : 0,
            fresh ? 1 : 0, pct, fresh ? usage!.Bytes : null, total,
            usage?.ScannedAt.ToUnixTimeSeconds());
    }
}
