using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class OutputPathPlannerTests
{
    [Fact]
    public void FormatDate_ReplacesTokens()
    {
        var dt = new DateTime(2026, 7, 5);
        Assert.Equal("2026-07-05", OutputPathPlanner.FormatDate(dt, "YYYY-MM-DD"));
    }

    [Fact]
    public void FormatTime_ReplacesTokens()
    {
        var dt = new DateTime(2026, 7, 5, 9, 5, 3);
        Assert.Equal("09-05-03", OutputPathPlanner.FormatTime(dt, "hh-mm-ss"));
    }

    [Fact]
    public void ComputeNextBoundary_AlignsToGridFromMidnight()
    {
        var now = new DateTime(2026, 7, 5, 0, 12, 0);
        var boundary = OutputPathPlanner.ComputeNextBoundary(now, 600);
        Assert.Equal(new DateTime(2026, 7, 5, 0, 20, 0), boundary);
    }

    [Fact]
    public void ComputeNextBoundary_ExactlyOnBoundary_ReturnsNextOne()
    {
        var now = new DateTime(2026, 7, 5, 0, 20, 0);
        var boundary = OutputPathPlanner.ComputeNextBoundary(now, 600);
        Assert.Equal(new DateTime(2026, 7, 5, 0, 30, 0), boundary);
    }

    [Fact]
    public void ParseDateFolder_ValidName_ReturnsDate()
    {
        var result = OutputPathPlanner.ParseDateFolder("2026-07-05", "YYYY-MM-DD");
        Assert.Equal(new DateOnly(2026, 7, 5), result);
    }

    [Fact]
    public void ParseDateFolder_InvalidName_ReturnsNull()
    {
        Assert.Null(OutputPathPlanner.ParseDateFolder("not-a-date", "YYYY-MM-DD"));
    }
}
