using System.Text.RegularExpressions;

namespace Quince.Service.Audio;

public static class OutputPathPlanner
{
    public static string FormatDate(DateTime dt, string format) =>
        format.Replace("YYYY", dt.Year.ToString("D4"))
              .Replace("MM", dt.Month.ToString("D2"))
              .Replace("DD", dt.Day.ToString("D2"));

    public static string FormatTime(DateTime dt, string format) =>
        format.Replace("hh", dt.Hour.ToString("D2"))
              .Replace("mm", dt.Minute.ToString("D2"))
              .Replace("ss", dt.Second.ToString("D2"));

    /// <summary>Next file-rotation boundary after <paramref name="now"/>, aligned to a
    /// <paramref name="durationSeconds"/> grid measured from midnight.</summary>
    public static DateTime ComputeNextBoundary(DateTime now, int durationSeconds)
    {
        var midnight = now.Date;
        var elapsed = (now - midnight).TotalSeconds;
        var nextElapsed = Math.Ceiling((elapsed + 1e-9) / durationSeconds) * durationSeconds;
        return midnight.AddSeconds(nextElapsed);
    }

    /// <summary>Parses a date-folder name (e.g. "2026-07-05") back to a date using the
    /// same YYYY/MM/DD token format used to create it. Returns null if it doesn't match.</summary>
    public static DateOnly? ParseDateFolder(string name, string format)
    {
        var pattern = "^" + Regex.Escape(format)
            .Replace("YYYY", @"(?<year>\d{4})")
            .Replace("MM", @"(?<month>\d{2})")
            .Replace("DD", @"(?<day>\d{2})") + "$";
        var m = Regex.Match(name, pattern);
        if (!m.Success) return null;
        return new DateOnly(int.Parse(m.Groups["year"].Value), int.Parse(m.Groups["month"].Value), int.Parse(m.Groups["day"].Value));
    }
}
