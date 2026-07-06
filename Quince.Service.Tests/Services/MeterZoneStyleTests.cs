using Quince.Service.Configuration;
using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class MeterZoneStyleTests
{
    [Fact]
    public void Percent_NegativeInfinity_IsZero()
    {
        Assert.Equal(0, MeterZoneStyle.Percent(double.NegativeInfinity));
    }

    [Fact]
    public void Percent_ZeroDb_IsFullScale()
    {
        Assert.Equal(100, MeterZoneStyle.Percent(0));
    }

    [Fact]
    public void Percent_MinusSixtyDb_IsZero()
    {
        Assert.Equal(0, MeterZoneStyle.Percent(-60));
    }

    [Fact]
    public void Percent_ClampsAboveFullScale()
    {
        Assert.Equal(100, MeterZoneStyle.Percent(10));
    }

    [Fact]
    public void PercentText_UsesInvariantDecimalSeparator()
    {
        Assert.DoesNotContain(',', MeterZoneStyle.PercentText(-30));
        Assert.Contains('.', MeterZoneStyle.PercentText(-29.5));
    }

    [Fact]
    public void BuildTrackGradient_HorizontalUsesToRight()
    {
        var colors = new MeterColorsConfig { ZoneYellowDb = -18, ZoneRedDb = -6, ColorGreen = "#111", ColorYellow = "#222", ColorRed = "#333" };
        var gradient = MeterZoneStyle.BuildTrackGradient(colors, vertical: false);

        Assert.StartsWith("linear-gradient(to right,", gradient);
        Assert.Contains("#111", gradient);
        Assert.Contains("#222", gradient);
        Assert.Contains("#333", gradient);
    }

    [Fact]
    public void BuildTrackGradient_VerticalUsesToTop()
    {
        var colors = new MeterColorsConfig();
        var gradient = MeterZoneStyle.BuildTrackGradient(colors, vertical: true);

        Assert.StartsWith("linear-gradient(to top,", gradient);
    }

    [Fact]
    public void BuildTrackGradient_ZoneBoundariesMatchConfiguredThresholds()
    {
        var colors = new MeterColorsConfig { ZoneYellowDb = -30, ZoneRedDb = -30 }; // both zones at the same point -> same percent text
        var gradient = MeterZoneStyle.BuildTrackGradient(colors, vertical: false);
        var expectedPct = MeterZoneStyle.PercentText(-30);

        // The stop percentage should appear (as text) at both the green->yellow and yellow->red boundary.
        Assert.Contains($"{expectedPct}%", gradient);
    }
}
