using Quince.Service.Configuration;

namespace Quince.Service.Services;

public static class ChannelDisplayFormatter
{
    public static string FormatSource(ChannelConfig config)
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
            return $"Soundcard: {name}" + (detail.Length > 0 ? $" · {detail}" : "");
        }

        var st = string.IsNullOrEmpty(src.StreamType) ? "STREAM" : src.StreamType.ToUpperInvariant();
        return $"Stream: {st} · {src.Url}";
    }

    public static string FormatOutput(ChannelConfig config)
    {
        var outFmt = config.OutputFormat;
        var fmt = outFmt.FileFormat.ToUpperInvariant();
        var quality = outFmt.FileFormat is "mp3" or "aac"
            ? $"{outFmt.BitrateKbps}kbps"
            : $"{outFmt.BitDepth}bit";
        var path = config.SavePath ?? "";
        var duration = FormatDuration(config.FileDurationSeconds);
        var parts = new[] { $"{fmt} {quality}", path, duration }.Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" · ", parts);
    }

    public static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
            return "0 с";

        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var secs = seconds % 60;

        if (hours > 0 && minutes == 0 && secs == 0)
            return $"{hours} ч";
        if (hours > 0)
        {
            var parts = new List<string> { $"{hours} ч" };
            if (minutes > 0) parts.Add($"{minutes} мин");
            if (secs > 0) parts.Add($"{secs} с");
            return string.Join(" ", parts);
        }
        if (minutes > 0 && secs == 0)
            return $"{minutes} мин";
        if (minutes > 0)
            return $"{minutes} мин {secs} с";
        return $"{secs} с";
    }
}
