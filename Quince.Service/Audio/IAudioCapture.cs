using System.Threading.Channels;

namespace Quince.Service.Audio;

/// <summary>
/// Shared shape for audio capture backends (network stream via ffmpeg, local soundcard via BASS, ...).
/// Downstream consumers (<see cref="AudioWriter"/>, <see cref="LevelMeter"/>, <see cref="SilenceDetector"/>)
/// only depend on this interface and don't care how the <see cref="AudioChunk"/>s were produced.
/// </summary>
public interface IAudioCapture
{
    int SampleRate { get; }
    int Channels { get; }
    StreamStatus Status { get; }
    int ReconnectAttempt { get; }

    ChannelReader<AudioChunk> Subscribe(string consumerId);
    void Unsubscribe(string consumerId);
    void Start();
    void Stop();
}
