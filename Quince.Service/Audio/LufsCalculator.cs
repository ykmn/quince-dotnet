namespace Quince.Service.Audio;

public static class LufsCalculator
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateOffsetLu = -10.0;

    /// <summary>Converts a summed-across-channels K-weighted mean-square value to LUFS.</summary>
    public static double FromMeanSquare(double totalMeanSquare)
    {
        // Noise floor: below ~1e-10 (~-100 dBFS RMS) is treated as silence.
        if (totalMeanSquare < 1e-10) return double.NegativeInfinity;
        return -0.691 + 10.0 * Math.Log10(totalMeanSquare);
    }

    /// <summary>Gated integrated loudness (EBU R128) from accumulated 400ms blocks.
    /// <paramref name="blockLufs"/>[i] must be <c>FromMeanSquare(blockTotalMeanSquare[i])</c>.</summary>
    public static double Integrated(IReadOnlyList<double> blockLufs, IReadOnlyList<double> blockTotalMeanSquare)
    {
        if (blockLufs.Count == 0) return double.NegativeInfinity;

        double ungatedSum = 0;
        var ungatedCount = 0;
        for (var i = 0; i < blockLufs.Count; i++)
        {
            if (blockLufs[i] > AbsoluteGateLufs)
            {
                ungatedSum += blockTotalMeanSquare[i];
                ungatedCount++;
            }
        }
        if (ungatedCount == 0) return double.NegativeInfinity;

        var ungatedLufs = FromMeanSquare(ungatedSum / ungatedCount);
        var relThreshold = ungatedLufs + RelativeGateOffsetLu;

        double gatedSum = 0;
        var gatedCount = 0;
        for (var i = 0; i < blockLufs.Count; i++)
        {
            if (blockLufs[i] > AbsoluteGateLufs && blockLufs[i] > relThreshold)
            {
                gatedSum += blockTotalMeanSquare[i];
                gatedCount++;
            }
        }
        if (gatedCount == 0) return double.NegativeInfinity;

        return FromMeanSquare(gatedSum / gatedCount);
    }
}
