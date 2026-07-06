using System.Runtime.InteropServices;
using System.Threading.Channels;
using ManagedBass;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

/// <summary>
/// Captures audio from a local soundcard/input device via BASS (ManagedBass), for
/// <c>source.type: soundcard</c> channels. Mirrors the public shape and status-transition
/// behaviour of <see cref="StreamCapture"/> (Connecting → Streaming → Reconnecting → Stopped)
/// so downstream consumers and the UI's status/reconnect display work unmodified regardless of
/// which capture backend a channel uses.
/// </summary>
public sealed class SoundcardCapture : IAudioCapture
{
    private const int RecordSampleRate = 44100;
    private const int RecordChannels = 2;
    private const int MonitorPollMs = 500;
    private const int DisconnectConfirmMs = 300;

    public int SampleRate => RecordSampleRate;
    public int Channels => RecordChannels;

    private readonly SourceConfig _source;
    private readonly int _reconnectDelaySeconds;
    private readonly ILogger _log;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

    private volatile StreamStatus _status = StreamStatus.Stopped;
    private volatile int _reconnectAttempt;
    private int _recordHandle;
    private RecordProcedure? _recordProcedure;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public SoundcardCapture(SourceConfig source, int reconnectDelaySeconds, ILogger log)
    {
        _source = source;
        _reconnectDelaySeconds = Math.Max(1, reconnectDelaySeconds);
        _log = log;
    }

    public StreamStatus Status => _status;
    public int ReconnectAttempt => _reconnectAttempt;

    public ChannelReader<AudioChunk> Subscribe(string consumerId)
    {
        var channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_lock) { _consumers[consumerId] = channel.Writer; }
        return channel.Reader;
    }

    public void Unsubscribe(string consumerId)
    {
        lock (_lock) { _consumers.Remove(consumerId); }
    }

    public void Start()
    {
        if (_task is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _status = StreamStatus.Stopped;
        FreeRecordHandle();
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        _task = null;
    }

    internal readonly record struct RecordDeviceInfo(int Index, string Name, string Driver, bool IsEnabled, bool IsDefault);

    /// <summary>
    /// Picks which recording device index to use out of the devices currently reported by BASS.
    /// There is no legacy reference implementation to transliterate here, so this is a best-effort
    /// heuristic with the following priority (first applicable branch wins, no further fallback once
    /// a branch is entered — an explicitly configured UID/name that doesn't match anything intentionally
    /// resolves to "no device" rather than silently picking an unrelated one):
    ///   1. DeviceUid set        -> case-insensitive EXACT match against a device's Driver string.
    ///   2. else DeviceName set  -> case-insensitive EXACT match against Name, else case-insensitive
    ///                              SUBSTRING match against Name.
    ///   3. else DeviceIndex >=0 -> used as-is if in range and that device IsEnabled (an out-of-range
    ///                              or disabled index falls through to the next tier instead of throwing).
    ///   4. else                 -> first device with IsDefault &amp;&amp; IsEnabled.
    ///   5. else                 -> first IsEnabled device.
    ///   6. else                 -> null (caller should log an error and treat it as a startup failure,
    ///                              retrying on the same cadence as a network reconnect).
    /// </summary>
    internal static int? ResolveDeviceIndex(IReadOnlyList<RecordDeviceInfo> devices, string deviceUid, string deviceName, int deviceIndex)
    {
        if (!string.IsNullOrEmpty(deviceUid))
        {
            var match = FindFirst(devices, d => string.Equals(d.Driver, deviceUid, StringComparison.OrdinalIgnoreCase));
            return match?.Index;
        }

        if (!string.IsNullOrEmpty(deviceName))
        {
            var exact = FindFirst(devices, d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.Value.Index;

            var substring = FindFirst(devices, d => d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
            return substring?.Index;
        }

        if (deviceIndex >= 0 && deviceIndex < devices.Count && devices[deviceIndex].IsEnabled)
            return devices[deviceIndex].Index;

        var defaultDevice = FindFirst(devices, d => d.IsDefault && d.IsEnabled);
        if (defaultDevice != null) return defaultDevice.Value.Index;

        var anyEnabled = FindFirst(devices, d => d.IsEnabled);
        return anyEnabled?.Index;
    }

    private static RecordDeviceInfo? FindFirst(IReadOnlyList<RecordDeviceInfo> devices, Func<RecordDeviceInfo, bool> predicate)
    {
        foreach (var device in devices)
        {
            if (predicate(device)) return device;
        }
        return null;
    }

    internal static List<RecordDeviceInfo> EnumerateDevices()
    {
        var devices = new List<RecordDeviceInfo>();
        var i = 0;
        while (Bass.RecordGetDeviceInfo(i, out var info))
        {
            // On Windows/WASAPI, BASS's recording-device enumeration also includes "loopback"
            // devices — captures of an OUTPUT device's playback stream, listed under names like
            // the speaker/headphone device they mirror. Those aren't audio inputs and would show
            // up as "outputs" in the device picker, so they're excluded here. Device indices
            // still line up 1:1 with what BASS itself reports (i keeps incrementing regardless),
            // so ResolveDeviceIndex's index-based fallback isn't affected by the filtering.
            if (!info.IsLoopback)
                devices.Add(new RecordDeviceInfo(i, info.Name ?? "", info.Driver ?? "", info.IsEnabled, info.IsDefault));
            i++;
        }
        return devices;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _reconnectAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            _status = StreamStatus.Connecting;
            _log.LogInformation("Подключение к аудиоустройству (попытка {Attempt})", _reconnectAttempt);

            try
            {
                var devices = EnumerateDevices();
                var deviceIndex = ResolveDeviceIndex(devices, _source.DeviceUid, _source.DeviceName, _source.DeviceIndex);
                if (deviceIndex == null)
                {
                    throw new InvalidOperationException("Не найдено подходящее устройство записи звука");
                }

                var deviceInfo = devices.First(d => d.Index == deviceIndex.Value);
                _log.LogInformation("Выбрано устройство записи: {Name} (индекс {Index})", deviceInfo.Name, deviceIndex.Value);

                if (!Bass.RecordInit(deviceIndex.Value) && Bass.LastError != Errors.Already)
                {
                    throw new InvalidOperationException($"RecordInit не удался: {Bass.LastError}");
                }

                Bass.CurrentRecordingDevice = deviceIndex.Value;

                _recordProcedure = RecordProc;
                var handle = Bass.RecordStart(SampleRate, Channels, BassFlags.Float, _recordProcedure, IntPtr.Zero);
                if (handle == 0)
                {
                    throw new InvalidOperationException($"RecordStart не удался: {Bass.LastError}");
                }

                _recordHandle = handle;
                _status = StreamStatus.Streaming;
                if (_reconnectAttempt > 0)
                    _log.LogInformation("Переподключение к аудиоустройству выполнено");
                _reconnectAttempt = 0;

                while (!ct.IsCancellationRequested)
                {
                    // Only BASS_ACTIVE_STOPPED means the channel actually terminated (device
                    // removed/disabled, driver error). "Stalled" just means BASS is momentarily
                    // waiting for data — normal for quiet/silent input (e.g. an idle virtual audio
                    // cable with nothing currently playing into it) and not a disconnection; treating
                    // it as one caused spurious reconnect loops on otherwise-healthy devices.
                    if (Bass.ChannelIsActive(handle) == PlaybackState.Stopped)
                    {
                        // Debounce: some virtual/loopback devices report a single momentary
                        // Stopped reading that clears itself right away (e.g. while a paired
                        // playback app briefly reconfigures the stream) — confirm it's still
                        // stopped a moment later before tearing down and reconnecting.
                        await Task.Delay(DisconnectConfirmMs, ct);
                        if (Bass.ChannelIsActive(handle) != PlaybackState.Stopped) continue;

                        // Distinguish "the device itself vanished" from "BASS's handle died while
                        // the device is still there" — e.g. BASS_ERROR_HANDLE (as opposed to a
                        // driver-level removal) means the handle itself was invalidated (some
                        // virtual audio cables reset their capture pin when idle/reconfigured),
                        // which the earlier Stopped-vs-Stalled and debounce fixes don't help with,
                        // since the handle is genuinely dead either way — reconnecting is the only
                        // recovery. Logged for diagnosis if this keeps recurring.
                        var stillEnumerated = EnumerateDevices().Any(d => d.Index == deviceIndex.Value);
                        _log.LogWarning(
                            "Запись с устройства прекратилась неожиданно (код ошибки BASS: {Error}, устройство всё ещё видно системе: {StillEnumerated})",
                            Bass.LastError, stillEnumerated);
                        break;
                    }
                    await Task.Delay(MonitorPollMs, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Ошибка захвата звука с устройства");
            }
            finally
            {
                FreeRecordHandle();
            }

            if (ct.IsCancellationRequested) break;

            _reconnectAttempt++;
            _status = StreamStatus.Reconnecting;
            _log.LogWarning("Устройство записи отключено. Попытка переподключения {Attempt} через {Delay}с",
                _reconnectAttempt, _reconnectDelaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(_reconnectDelaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _status = StreamStatus.Stopped;
    }

    private bool RecordProc(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return false; // false = continue recording

        var sampleCount = length / sizeof(float);
        var frameCount = sampleCount / Channels;
        if (frameCount == 0) return false;

        sampleCount = frameCount * Channels;
        var samples = new float[sampleCount];
        Marshal.Copy(buffer, samples, 0, sampleCount);
        var chunk = new AudioChunk(samples, Channels);

        List<KeyValuePair<string, ChannelWriter<AudioChunk>>> consumers;
        lock (_lock) { consumers = _consumers.ToList(); }

        foreach (var (consumerId, writer) in consumers)
        {
            if (!writer.TryWrite(chunk))
                _log.LogDebug("Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", consumerId, frameCount);
        }

        return false; // continue recording
    }

    /// <summary>
    /// Stops and frees only this channel's own recording handle. Deliberately does NOT call
    /// Bass.RecordFree() — the underlying device may be shared with other soundcard channels that
    /// initialized the same device, and freeing it here would pull the rug out from under them.
    /// </summary>
    private void FreeRecordHandle()
    {
        var handle = _recordHandle;
        _recordHandle = 0;
        if (handle == 0) return;

        try { Bass.ChannelStop(handle); } catch { /* already stopped/freed */ }
        try { Bass.StreamFree(handle); } catch { /* already freed */ }
    }
}
