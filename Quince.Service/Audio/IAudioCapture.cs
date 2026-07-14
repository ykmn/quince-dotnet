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

    /// <summary>OS process ID of this backend's own subprocess while it's running, for the admin
    /// "Монитор ресурсов" dialog (<see cref="Services.ProcessMonitorService"/>) — null for a backend
    /// with no subprocess of its own (<see cref="SoundcardCapture"/>, in-process via BASS) or while
    /// stopped/reconnecting between attempts.</summary>
    int? ProcessId { get; }

    ChannelReader<AudioChunk> Subscribe(string consumerId);
    void Unsubscribe(string consumerId);
    void Start();
    void Stop();
}
