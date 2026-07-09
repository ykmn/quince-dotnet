using Microsoft.Extensions.Logging;

namespace Quince.Service.Services;

/// <summary>
/// Tracks which channel is currently being "audition-ed" (monitored) for live playback in the
/// browser — independent of recording. Only one channel can play at a time; starting playback on a
/// different channel stops whatever was already playing. The actual audio bytes are delivered to
/// the browser by <see cref="AudioStreamEndpoint"/> over HTTP (an &lt;audio&gt; element in the
/// client plays them through whatever output device the browser/OS currently defaults to — per-
/// device selection would need a secure context for <c>HTMLMediaElement.setSinkId()</c>, deferred).
/// This class only owns the "which channel, ensure it's running" bookkeeping so the UI (channel
/// cards) can reflect Play/Stop state; it does not touch audio hardware itself.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private readonly AudioEngineManager _engineManager;
    private readonly ILogger<AudioPlaybackService> _log;

    private readonly object _lock = new();
    private string? _playingChannel;
    private bool _autoStarted;

    /// <summary>Fired whenever playback starts or stops, so UI (channel cards, the browser
    /// &lt;audio&gt; element via MainLayout) can react.</summary>
    public event Action? Changed;

    public AudioPlaybackService(AudioEngineManager engineManager, ILogger<AudioPlaybackService> log)
    {
        _engineManager = engineManager;
        _log = log;
    }

    public string? PlayingChannel { get { lock (_lock) { return _playingChannel; } } }

    /// <summary>Marks <paramref name="channelName"/> as the channel to monitor, stopping any
    /// channel already playing first. The channel must currently be running (recording or not —
    /// just capturing) for there to be any audio to stream; if it isn't, this starts its capture
    /// pipeline just for the duration of playback and stops it again in <see cref="Stop"/> (only if
    /// we're the one who started it — a channel already recording keeps going).</summary>
    public void Play(string channelName)
    {
        Stop();

        var autoStarted = false;
        if (!_engineManager.IsRunning(channelName))
        {
            // suppressRecording: true — this auto-start exists purely to have audio to stream for
            // listen-in; it must not silently begin writing a real recording file as a side effect
            // of clicking ▶ on a stopped channel (docs/HISTORY.md #64).
            _engineManager.Start(channelName, suppressRecording: true);
            autoStarted = true;
        }

        lock (_lock)
        {
            _playingChannel = channelName;
            _autoStarted = autoStarted;
        }
        using (_log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName }))
            _log.LogInformation("Прослушивание начато (браузер)");
        Changed?.Invoke();
    }

    public void Stop()
    {
        string? channelName;
        bool autoStarted;

        lock (_lock)
        {
            channelName = _playingChannel;
            autoStarted = _autoStarted;
            _playingChannel = null;
            _autoStarted = false;
        }

        if (channelName == null) return;

        if (autoStarted) _engineManager.Stop(channelName);
        using (_log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName }))
            _log.LogInformation("Прослушивание остановлено");
        Changed?.Invoke();
    }

    /// <summary>Clears playback state if the given channel stopped on its own (e.g. reconnect
    /// exhausted) while it was being monitored, without touching any other channel that might have
    /// started playing in the meantime.</summary>
    public void StopIfStillPlaying(string channelName)
    {
        bool isThisChannel;
        lock (_lock) { isThisChannel = _playingChannel == channelName; }
        if (isThisChannel) Stop();
    }

    public void Dispose() => Stop();
}
