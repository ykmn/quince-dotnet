namespace Quince.Service.Audio;

/// <summary>
/// Decimated raw (pre-K-weighting) L/R sample pairs from one processed chunk, capped at
/// roughly 256 points so the payload pushed to the browser stays small even though this
/// fires ~10x/second.
/// </summary>
public sealed record GoniometerFrame(float[] Left, float[] Right)
{
    /// <summary>
    /// Picks up to <paramref name="maxPoints"/> evenly-spaced samples from <paramref name="left"/>/
    /// <paramref name="right"/> (which must be the same length) via a fixed stride, and casts them
    /// down to float for transport. Pure/testable — no dependency on Bass/ffmpeg/live audio.
    /// </summary>
    internal static GoniometerFrame Decimate(double[] left, double[] right, int maxPoints)
    {
        var frames = left.Length;
        if (frames == 0) return new GoniometerFrame(Array.Empty<float>(), Array.Empty<float>());

        var stride = Math.Max(1, frames / maxPoints);
        var count = (frames + stride - 1) / stride;

        var outLeft = new float[count];
        var outRight = new float[count];
        var j = 0;
        for (var i = 0; i < frames; i += stride)
        {
            outLeft[j] = (float)left[i];
            outRight[j] = (float)right[i];
            j++;
        }

        return new GoniometerFrame(outLeft, outRight);
    }
}
