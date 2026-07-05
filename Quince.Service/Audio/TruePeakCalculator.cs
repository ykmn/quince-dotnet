namespace Quince.Service.Audio;

public static class TruePeakCalculator
{
    /// <summary>True Peak (dBTP) of one channel's samples via 4x oversampling
    /// (linear interpolation).</summary>
    public static double TruePeakDb(ReadOnlySpan<double> channelSamples)
    {
        var n = channelSamples.Length;
        if (n == 0) return double.NegativeInfinity;

        var nUp = n * 4;
        var maxAbs = 0.0;
        var denom = nUp > 1 ? nUp - 1 : 1;

        for (var i = 0; i < nUp; i++)
        {
            var pos = (double)i * (n - 1) / denom;
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, n - 1);
            var frac = pos - lo;
            var value = channelSamples[lo] + frac * (channelSamples[hi] - channelSamples[lo]);
            var abs = Math.Abs(value);
            if (abs > maxAbs) maxAbs = abs;
        }

        return 20.0 * Math.Log10(Math.Max(maxAbs, 1e-9));
    }
}
