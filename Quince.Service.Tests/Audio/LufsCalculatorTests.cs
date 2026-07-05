using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class LufsCalculatorTests
{
    [Fact]
    public void FromMeanSquare_BelowNoiseFloor_ReturnsNegativeInfinity()
    {
        Assert.Equal(double.NegativeInfinity, LufsCalculator.FromMeanSquare(1e-12));
    }

    [Fact]
    public void FromMeanSquare_UnityMeanSquare_MatchesReferenceFormula()
    {
        Assert.Equal(-0.691, LufsCalculator.FromMeanSquare(1.0), precision: 6);
    }

    [Fact]
    public void Integrated_NoBlocks_ReturnsNegativeInfinity()
    {
        Assert.Equal(double.NegativeInfinity, LufsCalculator.Integrated(new List<double>(), new List<double>()));
    }

    [Fact]
    public void Integrated_AllBlocksBelowAbsoluteGate_ReturnsNegativeInfinity()
    {
        var lufs = new List<double> { -80.0, -75.0 };
        var ms = new List<double> { 1e-8, 1e-8 };
        Assert.Equal(double.NegativeInfinity, LufsCalculator.Integrated(lufs, ms));
    }

    [Fact]
    public void Integrated_ConsistentLoudBlocks_ReturnsFiniteValue()
    {
        var lufs = new List<double> { -0.691, -0.691, -0.691 };
        var ms = new List<double> { 1.0, 1.0, 1.0 };

        var result = LufsCalculator.Integrated(lufs, ms);

        Assert.Equal(-0.691, result, precision: 6);
    }
}
