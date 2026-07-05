namespace Quince.Service.Audio;

/// <summary>Interleaved PCM float32 audio, e.g. [L0, R0, L1, R1, ...] for stereo.</summary>
public readonly struct AudioChunk
{
    public AudioChunk(float[] samples, int channels)
    {
        Samples = samples;
        Channels = channels;
    }

    public float[] Samples { get; }
    public int Channels { get; }
    public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;
}
