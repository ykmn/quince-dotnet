using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class KWeightingFilterTests
{
    [Fact]
    public void Apply_BlocksDcOverTime()
    {
        var filter = new KWeightingFilter(44100);
        var samples = new double[44100]; // 1 second of full-scale DC
        for (int i = 0; i < samples.Length; i++) samples[i] = 1.0;

        filter.Apply(new[] { samples });

        // The RLB high-pass stage rolls off DC/infrasonic content; after 1s
        // of settling the tail should be far smaller than the 1.0 input.
        Assert.True(Math.Abs(samples[^1]) < 0.01);
    }

    [Fact]
    public void Apply_IsLinearAndSymmetricAcrossChannels()
    {
        var filter = new KWeightingFilter(44100);
        var left = new double[100];
        var right = new double[100];
        for (int i = 0; i < 100; i++) { left[i] = 1.0; right[i] = -1.0; }

        filter.Apply(new[] { left, right });

        Assert.Equal(-left[^1], right[^1], precision: 8);
    }
}
