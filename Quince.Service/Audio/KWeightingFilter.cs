namespace Quince.Service.Audio;

/// <summary>Two-stage K-weighting filter (EBU R128 / ITU-R BS.1770-4), coefficients
/// derived per sample rate via bilinear transform of the EBU Tech 3341 analog prototype.</summary>
public sealed class KWeightingFilter
{
    // Stage 1 - high-shelf pre-filter (acoustic head effect)
    private const double S1F0 = 1681.974450955533;
    private const double S1G = 3.999843853973347;
    private const double S1Q = 0.7071752369554196;
    // Stage 2 - RLB-weighted high-pass
    private const double S2F0 = 38.13547087602444;
    private const double S2Q = 0.5003270373238773;

    private readonly double[] _b1;
    private readonly double[] _a1;
    private readonly double[] _b2;
    private readonly double[] _a2;

    private double[,]? _zi1;
    private double[,]? _zi2;

    public KWeightingFilter(int sampleRate)
    {
        var k1 = Math.Tan(Math.PI * S1F0 / sampleRate);
        var vh = Math.Pow(10.0, S1G / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a01 = 1.0 + k1 / S1Q + k1 * k1;
        _b1 = new[]
        {
            (vh + vb * k1 / S1Q + k1 * k1) / a01,
            2.0 * (k1 * k1 - vh) / a01,
            (vh - vb * k1 / S1Q + k1 * k1) / a01,
        };
        _a1 = new[]
        {
            1.0,
            2.0 * (k1 * k1 - 1.0) / a01,
            (1.0 - k1 / S1Q + k1 * k1) / a01,
        };

        var k2 = Math.Tan(Math.PI * S2F0 / sampleRate);
        var a02 = 1.0 + k2 / S2Q + k2 * k2;
        _b2 = new[] { 1.0, -2.0, 1.0 };
        _a2 = new[]
        {
            1.0,
            2.0 * (k2 * k2 - 1.0) / a02,
            (1.0 - k2 / S2Q + k2 * k2) / a02,
        };
    }

    /// <summary>Applies the cascade in place. Each inner array is one channel's samples.</summary>
    public void Apply(double[][] perChannelSamples)
    {
        var channels = perChannelSamples.Length;
        _zi1 ??= new double[2, channels];
        _zi2 ??= new double[2, channels];

        for (var ch = 0; ch < channels; ch++)
        {
            var data = perChannelSamples[ch];
            double z10 = _zi1[0, ch], z11 = _zi1[1, ch];
            double z20 = _zi2[0, ch], z21 = _zi2[1, ch];

            for (var f = 0; f < data.Length; f++)
            {
                var x = data[f];

                var y1 = _b1[0] * x + z10;
                z10 = _b1[1] * x - _a1[1] * y1 + z11;
                z11 = _b1[2] * x - _a1[2] * y1;

                var y2 = _b2[0] * y1 + z20;
                z20 = _b2[1] * y1 - _a2[1] * y2 + z21;
                z21 = _b2[2] * y1 - _a2[2] * y2;

                data[f] = y2;
            }

            _zi1[0, ch] = z10; _zi1[1, ch] = z11;
            _zi2[0, ch] = z20; _zi2[1, ch] = z21;
        }
    }

    public void Reset()
    {
        _zi1 = null;
        _zi2 = null;
    }
}
