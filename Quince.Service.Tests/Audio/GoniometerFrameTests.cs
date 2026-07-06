using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class GoniometerFrameTests
{
    [Fact]
    public void Decimate_LargeInput_CapsAtMaxPoints()
    {
        const int frames = 4096;
        var left = new double[frames];
        var right = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = i;
            right[i] = -i;
        }

        var frame = GoniometerFrame.Decimate(left, right, maxPoints: 256);

        Assert.True(frame.Left.Length <= 256);
        Assert.Equal(frame.Left.Length, frame.Right.Length);
        Assert.True(frame.Left.Length > 0);
    }

    [Fact]
    public void Decimate_ValuesAreDrawnFromInput_NotZeroedOrGarbage()
    {
        const int frames = 1000;
        var left = new double[frames];
        var right = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = i;
            right[i] = -i;
        }

        var frame = GoniometerFrame.Decimate(left, right, maxPoints: 256);

        // Every decimated point should equal -1 * the paired left value (since right = -left
        // at every original index), and each left value must be one of the original samples
        // (i.e. an exact double->float cast of some i in [0, frames)).
        for (var i = 0; i < frame.Left.Length; i++)
        {
            Assert.Equal(-frame.Left[i], frame.Right[i]);
            Assert.True(frame.Left[i] >= 0 && frame.Left[i] < frames);
        }

        // The stride must actually skip samples rather than always taking index 0.
        Assert.True(frame.Left.Length < frames);
        Assert.Contains(frame.Left, v => v > 0);
    }

    [Fact]
    public void Decimate_InputSmallerThanMaxPoints_ReturnsAllSamples()
    {
        var left = new double[] { 0.1, 0.2, 0.3 };
        var right = new double[] { -0.1, -0.2, -0.3 };

        var frame = GoniometerFrame.Decimate(left, right, maxPoints: 256);

        Assert.Equal(3, frame.Left.Length);
        Assert.Equal(0.1f, frame.Left[0]);
        Assert.Equal(0.2f, frame.Left[1]);
        Assert.Equal(0.3f, frame.Left[2]);
    }

    [Fact]
    public void Decimate_EmptyInput_ReturnsEmptyArrays()
    {
        var frame = GoniometerFrame.Decimate(Array.Empty<double>(), Array.Empty<double>(), maxPoints: 256);

        Assert.Empty(frame.Left);
        Assert.Empty(frame.Right);
    }
}
