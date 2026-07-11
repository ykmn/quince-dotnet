using Quince.Service.Audio;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>Which direction (if any) a channel's <see cref="SourceConfig.Url"/> disagrees with its
/// declared <see cref="SourceConfig.StreamType"/>, per <see cref="ChannelDisplayFormatter.DetectStreamTypeMismatch"/>.</summary>
public enum StreamTypeMismatch
{
    None,
    /// <summary>URL looks like an HLS playlist (contains ".m3u8") but StreamType isn't "hls".</summary>
    UrlLooksHlsButTypeIsNot,
    /// <summary>StreamType is "hls" but the URL doesn't look like an HLS playlist.</summary>
    TypeIsHlsButUrlDoesNot,
}

/// <summary>Which of the three states a channel's <see cref="EngineStatus"/> currently falls into —
/// the same 3-way split the channel card's status dot already uses (see
/// <see cref="ChannelDisplayFormatter.ClassifyRunState"/>), extracted so the main list's status
/// filter can reuse it without risking silently drifting from what the dot itself shows.</summary>
public enum ChannelRunState
{
    Stopped,
    Running,
    Error,
}

public static class ChannelDisplayFormatter
{
    /// <summary>Mirrors <c>ChannelCard.StatusDotClass</c>'s own switch exactly (green/red/grey) —
    /// a channel actively capturing for any reason (including a listen-in-only auto-start) counts as
    /// <see cref="ChannelRunState.Running"/> regardless of <see cref="EngineStatus.IsFileRecording"/>.</summary>
    public static ChannelRunState ClassifyRunState(EngineStatus status) => status switch
    {
        { IsRecording: true } => ChannelRunState.Running,
        { HasError: true } => ChannelRunState.Error,
        _ => ChannelRunState.Stopped,
    };

    /// <summary>Heuristic-only, non-blocking check — same ".m3u8" substring test already used by
    /// <c>scripts/Import-RadioPlayerStations.ps1</c> for bulk station import, just not previously
    /// wired into the live app. A mismatch here means <see cref="AudioWriter.ResolveEffectiveFormat"/>
    /// (which trusts <see cref="SourceConfig.StreamType"/> unconditionally) is likely picking the
    /// wrong codec for "original" output mode — see docs/HISTORY.md #62.</summary>
    public static StreamTypeMismatch DetectStreamTypeMismatch(SourceConfig source)
    {
        if (string.IsNullOrWhiteSpace(source.Url)) return StreamTypeMismatch.None;
        var urlLooksHls = source.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        var typeIsHls = source.StreamType == "hls";
        if (urlLooksHls && !typeIsHls) return StreamTypeMismatch.UrlLooksHlsButTypeIsNot;
        if (!urlLooksHls && typeIsHls) return StreamTypeMismatch.TypeIsHlsButUrlDoesNot;
        return StreamTypeMismatch.None;
    }

    public static string FormatSource(ChannelConfig config)
    {
        var (prefix, url) = FormatSourceParts(config);
        return prefix + url;
    }

    /// <summary>Splits the source line into a static prefix and the raw URL, so callers (the channel
    /// card) can mask just the URL on click without hiding the surrounding "Stream: HLS ·" label.
    /// <see cref="Url"/> is empty for <c>soundcard</c> sources — there's nothing sensitive to mask,
    /// the device name lives entirely in the prefix.</summary>
    public static (string Prefix, string Url) FormatSourceParts(ChannelConfig config)
    {
        var src = config.Source;
        if (src.Type == "soundcard")
        {
            var name = string.IsNullOrEmpty(src.DeviceName) ? "Unknown device" : src.DeviceName;
            var inf = config.InputFormat;
            var chLabel = inf.Channels == 2 ? "Stereo" : inf.Channels == 1 ? "Mono" : $"{inf.Channels}ch";
            var sr = inf.SampleRate != 0 ? $"{inf.SampleRate}Hz" : "";
            var parts = new[] { sr, chLabel }.Where(p => !string.IsNullOrEmpty(p));
            var detail = string.Join(" ", parts);
            return ($"Soundcard: {name}" + (detail.Length > 0 ? $" · {detail}" : ""), "");
        }

        if (src.Type == "livewire")
        {
            var name = string.IsNullOrEmpty(src.LivewireChannelName) ? "?" : src.LivewireChannelName;
            var addr = LivewireAddressing.IsValidChannelNumber(src.LivewireChannelNumber)
                ? LivewireAddressing.ChannelToMulticastAddress(src.LivewireChannelNumber).ToString()
                : "?";
            return ($"Livewire: {src.LivewireChannelNumber} — {name} ({addr})", "");
        }

        var st = string.IsNullOrEmpty(src.StreamType) ? "STREAM" : src.StreamType.ToUpperInvariant();
        return ($"Stream: {st} · ", src.Url);
    }

    public static string FormatOutput(ChannelConfig config, LocalizationService loc)
    {
        var (formatQuality, path, duration) = FormatOutputParts(config, loc);
        var parts = new[] { formatQuality, path, duration }.Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" · ", parts);
    }

    /// <summary>Splits the output line into its three joined segments so the channel card can mask
    /// just <see cref="Path"/> on click, leaving format/quality/duration always visible.</summary>
    public static (string FormatQuality, string Path, string Duration) FormatOutputParts(ChannelConfig config, LocalizationService loc)
    {
        // "original" mode resolves its actual codec/extension from the source (see
        // AudioWriter.ResolveEffectiveFormat) rather than the raw config value, which can be a
        // stale/default leftover (e.g. "mp3") never actually used for that mode.
        var outFmt = AudioWriter.ResolveEffectiveFormat(config);
        var fmt = outFmt.FileFormat.ToUpperInvariant();
        var quality = outFmt.FileFormat is "mp3" or "aac"
            ? $"{outFmt.BitrateKbps}kbps"
            : $"{outFmt.BitDepth}bit";
        var path = config.SavePath ?? "";
        var duration = FormatDuration(config.FileDurationMinutes * 60, loc);
        return ($"{fmt} {quality}", path, duration);
    }

    public static string FormatDuration(int seconds, LocalizationService loc)
    {
        var h = loc["common.unitHours"];
        var m = loc["common.unitMinutes"];
        var s = loc["common.unitSeconds"];

        if (seconds <= 0)
            return $"0 {s}";

        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var secs = seconds % 60;

        if (hours > 0 && minutes == 0 && secs == 0)
            return $"{hours} {h}";
        if (hours > 0)
        {
            var parts = new List<string> { $"{hours} {h}" };
            if (minutes > 0) parts.Add($"{minutes} {m}");
            if (secs > 0) parts.Add($"{secs} {s}");
            return string.Join(" ", parts);
        }
        if (minutes > 0 && secs == 0)
            return $"{minutes} {m}";
        if (minutes > 0)
            return $"{minutes} {m} {secs} {s}";
        return $"{secs} {s}";
    }
}
