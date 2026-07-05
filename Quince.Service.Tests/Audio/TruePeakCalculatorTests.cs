using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class TruePeakCalculatorTests
{
    [Fact]
    public void TruePeakDb_FullScaleConstant_ReturnsZeroDb()
    {
        var samples = new double[100];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1.0;

        var result = TruePeakCalculator.TruePeakDb(samples);

        Assert.Equal(0.0, result, precision: 6);
    }

    [Fact]
    public void TruePeakDb_Silence_ReturnsVeryLowValue()
    {
        var samples = new double[100];
        var result = TruePeakCalculator.TruePeakDb(samples);
        Assert.True(result < -170.0);
    }

    [Fact]
    public void TruePeakDb_Empty_ReturnsNegativeInfinity()
    {
        Assert.Equal(double.NegativeInfinity, TruePeakCalculator.TruePeakDb(ReadOnlySpan<double>.Empty));
    }
}
