using System.Globalization;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>
/// Shared CSS-building logic for level meters: a segmented (not single-blended-color) bar where the
/// green/yellow/red zone boundaries come from <see cref="MeterColorsConfig"/>, so the colors along the
/// bar always reflect the configured thresholds rather than the current reading alone. Used by
/// ChannelCard's horizontal TP bar, IndicatorsPanel's vertical TP/LUFS meters, and the all-channels
/// list's horizontal L/R bars — kept in one place so the three don't drift.
/// </summary>
public static class MeterZoneStyle
{
    /// <summary>Maps a dB reading (-60..0 range) to a 0..100 fill percentage.</summary>
    public static double Percent(double db) => double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db + 60.0) / 60.0 * 100.0, 0, 100);

    /// <summary>Same as <see cref="Percent"/>, pre-formatted with an invariant decimal separator for use in CSS.</summary>
    public static string PercentText(double db) => Percent(db).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A hard-stop gradient spanning the whole 0..100% scale with segment boundaries at the configured
    /// zone thresholds — meant to sit on the *track* (always full size), with a same-color-as-track
    /// "cover" element hiding the portion above the current reading (see BuildCoverStyle below). This
    /// way each visible segment's color always corresponds to its actual zone on the scale, regardless
    /// of the current level, instead of the whole bar swapping to one blended color.
    /// </summary>
    public static string BuildTrackGradient(MeterColorsConfig colors, bool vertical)
    {
        var yellowPct = PercentText(colors.ZoneYellowDb);
        var redPct = PercentText(colors.ZoneRedDb);
        var direction = vertical ? "to top" : "to right";
        return $"linear-gradient({direction}, {colors.ColorGreen} 0%, {colors.ColorGreen} {yellowPct}%, " +
               $"{colors.ColorYellow} {yellowPct}%, {colors.ColorYellow} {redPct}%, " +
               $"{colors.ColorRed} {redPct}%, {colors.ColorRed} 100%)";
    }
}
