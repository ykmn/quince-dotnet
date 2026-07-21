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

    /// <summary>Recursively sums every file under <paramref name="path"/>, bounded by
    /// <paramref name="timeout"/> so a dead network share can't hang the caller forever. Shared by
    /// <see cref="Pages.Shared.ChannelEditDialog"/>'s single-channel "Прогноз объёма" and
    /// <see cref="Pages.Shared.UsageForecastDialog"/>'s all-channels forecast, which both need the
    /// exact same timeout/exception-handling shape — keeping one copy avoids the two drifting apart
    /// the way the single-channel version briefly did from <c>ValidateSavePathAsync</c>'s budget
    /// (docs/HISTORY.md #158/#160). Uses <see cref="DirectoryInfo.EnumerateFiles(string, SearchOption)"/>
    /// rather than <c>Directory.EnumerateFiles</c> + a fresh <c>new FileInfo(f)</c> per path — the
    /// latter issues a second stat() per file for no reason, since the first one already returns it.</summary>
    public static async Task<DiskUsageScanResult> ScanFolderSizeAsync(string path, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(() => Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0L);
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            return completed != task
                ? DiskUsageScanResult.Timeout
                : DiskUsageScanResult.Success(await task);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return DiskUsageScanResult.Failed(ex.Message);
        }
    }
}

/// <summary>Outcome of <see cref="DiskUsageEstimator.ScanFolderSizeAsync"/> — exactly one of the three
/// factory methods applies, so callers switch on <see cref="TimedOut"/>/<see cref="Error"/> rather than
/// juggling a bare nullable long that can't distinguish "0 bytes" from "didn't finish".</summary>
public readonly record struct DiskUsageScanResult(long? Bytes, bool TimedOut, string? Error)
{
    public static DiskUsageScanResult Success(long bytes) => new(bytes, false, null);
    public static DiskUsageScanResult Timeout { get; } = new(null, true, null);
    public static DiskUsageScanResult Failed(string error) => new(null, false, error);
}
