using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Quince.Service.Audio;

namespace Quince.Service.Services;

/// <summary>
/// Monitors ("auditions") one channel's live audio through a local sound-card output — independent
/// of recording. Only one channel can play at a time; starting playback on a different channel
/// stops whatever was already playing. Both the capture side (<see cref="SoundcardCapture"/>) and
/// this output side use NAudio/WASAPI, so device UIDs/enumeration are symmetrical.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const string ConsumerId = "playback-monitor";

    private readonly AudioEngineManager _engineManager;
    private readonly AppSettingsService _appSettings;
    private readonly ILogger<AudioPlaybackService> _log;

    private readonly object _lock = new();
    private string? _playingChannel;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private WasapiOut? _output;
    private bool _autoStarted;

    /// <summary>Fired whenever playback starts or stops, so UI (channel cards) can refresh their Play/Stop button.</summary>
    public event Action? Changed;

    public AudioPlaybackService(AudioEngineManager engineManager, AppSettingsService appSettings, ILogger<AudioPlaybackService> log)
    {
        _engineManager = engineManager;
        _appSettings = appSettings;
        _log = log;
    }

    public string? PlayingChannel { get { lock (_lock) { return _playingChannel; } } }

    /// <summary>Starts playing <paramref name="channelName"/>'s live audio to the configured output
    /// device, stopping any channel already playing first. The channel must currently be running
    /// (recording or not — just capturing) for there to be any audio to subscribe to.</summary>
    public void Play(string channelName)
    {
        Stop();

        // The channel doesn't have to be recording to audition it — start its capture pipeline
        // just for the duration of playback if it wasn't already running, and stop it again in
        // Stop() (only if we're the one who started it; a channel already recording keeps going).
        var autoStarted = false;
        if (!_engineManager.IsRunning(channelName))
        {
            _engineManager.Start(channelName);
            autoStarted = true;
        }

        var reader = _engineManager.SubscribeAudio(channelName, ConsumerId);
        if (reader == null)
        {
            if (autoStarted) _engineManager.Stop(channelName);
            _log.LogWarning("Не удалось начать воспроизведение канала '{Channel}': канал не запущен", channelName);
            throw new InvalidOperationException("Канал не запущен — нечего воспроизводить.");
        }

        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            device = ResolveOutputDevice(_appSettings.Current.OutputDeviceUid);
            if (device == null)
                throw new InvalidOperationException("Не найдено подходящее устройство воспроизведения звука.");

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            var buffer = new BufferedWaveProvider(waveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            output.Init(buffer);
            output.Play();

            var cts = new CancellationTokenSource();
            lock (_lock)
            {
                _playingChannel = channelName;
                _autoStarted = autoStarted;
                _output = output;
                _cts = cts;
                _pumpTask = Task.Run(() => PumpAsync(channelName, reader, buffer, cts.Token));
            }
            _log.LogInformation("Воспроизведение канала '{Channel}' на устройство '{Device}'", channelName, device.FriendlyName);
        }
        catch (Exception ex)
        {
            _engineManager.UnsubscribeAudio(channelName, ConsumerId);
            if (autoStarted) _engineManager.Stop(channelName);
            try { output?.Dispose(); } catch { /* already disposed */ }
            _log.LogError(ex, "Не удалось начать воспроизведение канала '{Channel}'", channelName);
            throw;
        }
        finally
        {
            device?.Dispose();
            Changed?.Invoke();
        }
    }

    public void Stop()
    {
        string? channelName;
        CancellationTokenSource? cts;
        Task? pumpTask;
        WasapiOut? output;
        bool autoStarted;

        lock (_lock)
        {
            channelName = _playingChannel;
            cts = _cts;
            pumpTask = _pumpTask;
            output = _output;
            autoStarted = _autoStarted;
            _playingChannel = null;
            _cts = null;
            _pumpTask = null;
            _output = null;
            _autoStarted = false;
        }

        if (channelName == null) return;

        cts?.Cancel();
        try { pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        try { output?.Stop(); } catch { /* already stopped */ }
        try { output?.Dispose(); } catch { /* already disposed */ }
        _engineManager.UnsubscribeAudio(channelName, ConsumerId);
        if (autoStarted) _engineManager.Stop(channelName);
        Changed?.Invoke();
    }

    private async Task PumpAsync(string channelName, ChannelReader<AudioChunk> reader, BufferedWaveProvider buffer, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                var bytes = new byte[chunk.Samples.Length * sizeof(float)];
                System.Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, bytes.Length);
                buffer.AddSamples(bytes, 0, bytes.Length);
            }
            // The reader completed on its own (channel stopped/removed elsewhere) rather than via
            // our own Stop() cancelling it — tidy up so the UI doesn't keep showing "playing".
            if (!ct.IsCancellationRequested)
            {
                _log.LogInformation("Воспроизведение канала '{Channel}' остановлено — источник завершил передачу", channelName);
                StopIfStillPlaying(channelName);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка воспроизведения канала '{Channel}'", channelName);
        }
    }

    /// <summary>Runs Stop() on a separate task — called from within the pump task itself, so
    /// Stop()'s own wait on <c>_pumpTask</c> mustn't block synchronously on the caller.</summary>
    private void StopIfStillPlaying(string channelName)
    {
        bool isThisChannel;
        lock (_lock) { isThisChannel = _playingChannel == channelName; }
        if (isThisChannel) Task.Run(Stop);
    }

    /// <summary>Resolves the configured output device by UID, falling back to the system default
    /// render device if unset or no longer present.</summary>
    internal static MMDevice? ResolveOutputDevice(string deviceUid)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(deviceUid))
        {
            try { return enumerator.GetDevice(deviceUid); }
            catch (Exception ex) when (ex is COMException or ArgumentException) { /* fall through to default */ }
        }
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (COMException) { return null; }
    }

    /// <summary>Enumerates real playback (render) devices — the output-side analogue of
    /// <see cref="SoundcardCapture.EnumerateDevices"/>. Reuses its device-info shape since the
    /// fields (index/name/id/enabled/default) aren't capture-specific.</summary>
    internal static List<SoundcardCapture.RecordDeviceInfo> EnumerateDevices()
    {
        var devices = new List<SoundcardCapture.RecordDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (COMException) { /* no default render device configured */ }

        var index = 0;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new SoundcardCapture.RecordDeviceInfo(index, device.FriendlyName, device.ID, IsEnabled: true, IsDefault: device.ID == defaultId));
                index++;
            }
        }
        return devices;
    }

    public void Dispose() => Stop();
}
