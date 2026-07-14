using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>
/// Pure-logic sizing math for the horizontal disk-usage indicator on the channel edit dialog's
/// "Выход" tab: how much disk space this channel's recordings are projected to occupy at its
/// currently configured bitrate/format and retention period. Deliberately independent of
/// <see cref="ChannelConfig.FileDurationMinutes"/> — rotation only chops one continuous recording
/// into files of that length, it doesn't change the total bytes written per day, so the projection
/// is just bytes/second times the retention window.
/// </summary>
public static class DiskUsageEstimator
{
    /// <summary>Bytes/second the output writer's ffmpeg actually produces, using
    /// <see cref="AudioWriter.ResolveEffectiveFormat"/> so "original" mode (source-derived
    /// format/codec) is estimated the same way "custom" mode is. For lossy formats (mp3/aac) this
    /// exactly matches the real encode, since <see cref="AudioWriter.BuildEncodeArgs"/> always uses
    /// <see cref="OutputFormatConfig.BitrateKbps"/> regardless of mode. For WAV in "original" mode
    /// the real encode uses the live capture's native sample rate/channels rather than these
    /// configured defaults (unknown until the channel actually starts) — an accepted approximation
    /// for an estimate shown before the channel has ever run.</summary>
    public static long EstimateBytesPerSecond(ChannelConfig config)
    {
        var fmt = AudioWriter.ResolveEffectiveFormat(config);
        return fmt.FileFormat.ToLowerInvariant() switch
        {
            "wav" => (long)(fmt.BitDepth / 8) * fmt.Channels * fmt.SampleRate,
            _ => (long)fmt.BitrateKbps * 1000 / 8, // mp3/aac
        };
    }

    /// <summary>Projected total bytes across the whole retention window, assuming continuous 24/7
    /// recording (this app's normal operating mode). Null when
    /// <see cref="ChannelConfig.RetentionDays"/> is 0 or less — retention is unlimited, so there's no
    /// meaningful upper bound to project.</summary>
    public static long? EstimateTotalBytes(ChannelConfig config)
    {
        if (config.RetentionDays <= 0) return null;
        return EstimateBytesPerSecond(config) * config.RetentionDays * 86400L;
    }
}
