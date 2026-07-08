using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

/// <summary>
/// Captures audio from a local soundcard/input device via NAudio's WASAPI wrapper, for
/// <c>source.type: soundcard</c> channels. Mirrors the public shape and status-transition
/// behaviour of <see cref="StreamCapture"/> (Connecting → Streaming → Reconnecting → Stopped)
/// so downstream consumers and the UI's status/reconnect display work unmodified regardless of
/// which capture backend a channel uses.
/// </summary>
public sealed class SoundcardCapture : IAudioCapture
{
    private const int RecordSampleRate = 44100;
    private const int RecordChannels = 2;

    public int SampleRate => RecordSampleRate;
    public int Channels => RecordChannels;

    private readonly SourceConfig _source;
    private readonly Func<int> _getReconnectDelaySeconds;
    private readonly Func<int> _getMaxReconnectAttempts;
    private readonly Action? _onReconnectExhausted;
    private readonly ILogger _log;
    private readonly string _channelName;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

    private volatile StreamStatus _status = StreamStatus.Stopped;
    private volatile int _reconnectAttempt;
    private WasapiCapture? _capture;
    private MMDevice? _mmDevice;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public SoundcardCapture(SourceConfig source, Func<int> getReconnectDelaySeconds, Func<int> getMaxReconnectAttempts,
        ILogger log, Action? onReconnectExhausted = null, string channelName = "")
    {
        _source = source;
        _getReconnectDelaySeconds = getReconnectDelaySeconds;
        _getMaxReconnectAttempts = getMaxReconnectAttempts;
        _onReconnectExhausted = onReconnectExhausted;
        _log = log;
        _channelName = channelName;
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
        DisposeCapture();
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        _task = null;
    }

    internal readonly record struct RecordDeviceInfo(int Index, string Name, string Id, bool IsEnabled, bool IsDefault);

    /// <summary>
    /// Picks which recording device index to use out of the devices currently reported by
    /// Windows. There is no legacy reference implementation to transliterate here, so this is a
    /// best-effort heuristic with the following priority (first applicable branch wins, no further
    /// fallback once a branch is entered — an explicitly configured UID/name that doesn't match
    /// anything intentionally resolves to "no device" rather than silently picking an unrelated one):
    ///   1. DeviceUid set        -> case-insensitive EXACT match against a device's Id string.
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
            var match = FindFirst(devices, d => string.Equals(d.Id, deviceUid, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Enumerates real audio inputs via Windows Core Audio's <see cref="DataFlow.Capture"/>
    /// endpoint category. Unlike BASS's recording-device enumeration (which used to mix in WASAPI
    /// loopback/render devices, requiring a reactive IsLoopback filter), loopback capture in the
    /// Core Audio API is a structurally separate mechanism (<c>WasapiLoopbackCapture</c> against a
    /// RENDER device) that never appears in the Capture endpoint collection — so there is nothing
    /// to filter out here by construction.
    /// </summary>
    internal static List<RecordDeviceInfo> EnumerateDevices()
    {
        var devices = new List<RecordDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (COMException)
        {
            // No default capture device configured (e.g. every input disabled) — leave defaultId
            // null so nothing matches IsDefault below, instead of failing enumeration outright.
        }

        var index = 0;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new RecordDeviceInfo(index, device.FriendlyName, device.ID, IsEnabled: true, IsDefault: device.ID == defaultId));
                index++;
            }
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

            // Signalled from the RecordingStopped event — replaces the old poll-every-500ms loop
            // with an authoritative "the stream really ended, and here's why" callback from WASAPI
            // itself, instead of inferring device state from a polled activity flag.
            var stopSignal = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

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

                using var enumerator = new MMDeviceEnumerator();
                var mmDevice = enumerator.GetDevice(deviceInfo.Id);

                var capture = new WasapiCapture(mmDevice)
                {
                    WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels),
                };
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += (_, e) => stopSignal.TrySetResult(e.Exception);

                capture.StartRecording();
                _mmDevice = mmDevice;
                _capture = capture;
                _status = StreamStatus.Streaming;
                if (_reconnectAttempt > 0)
                    _log.LogInformation("Переподключение к аудиоустройству выполнено");
                _reconnectAttempt = 0;

                var cancelTask = Task.Delay(Timeout.Infinite, ct);
                var completed = await Task.WhenAny(stopSignal.Task, cancelTask);
                if (completed == cancelTask)
                    break; // Stop() was called — clean shutdown, no reconnect wanted.

                var stopException = stopSignal.Task.Result;
                var stillEnumerated = EnumerateDevices().Any(d => d.Id == deviceInfo.Id);
                _log.LogWarning(stopException,
                    "Запись с устройства прекратилась неожиданно (устройство всё ещё видно системе: {StillEnumerated})",
                    stillEnumerated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Ошибка захвата звука с устройства");
            }
            finally
            {
                DisposeCapture();
            }

            if (ct.IsCancellationRequested) break;

            _reconnectAttempt++;
            var maxAttempts = _getMaxReconnectAttempts();
            if (maxAttempts > 0 && _reconnectAttempt > maxAttempts)
            {
                _status = StreamStatus.Error;
                _log.LogError("Превышен предел попыток переподключения ({Max}) — канал останавливается", maxAttempts);
                if (_onReconnectExhausted != null) _ = Task.Run(_onReconnectExhausted);
                return;
            }

            _status = StreamStatus.Reconnecting;
            var delaySeconds = Math.Max(1, _getReconnectDelaySeconds());
            _log.LogWarning("Устройство записи отключено. Попытка переподключения {Attempt} через {Delay}с",
                _reconnectAttempt, delaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _status = StreamStatus.Stopped;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var sampleCount = e.BytesRecorded / sizeof(float);
        var frameCount = sampleCount / Channels;
        if (frameCount == 0) return;

        sampleCount = frameCount * Channels;
        var samples = new float[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, sampleCount * sizeof(float));
        var chunk = new AudioChunk(samples, Channels);

        List<KeyValuePair<string, ChannelWriter<AudioChunk>>> consumers;
        lock (_lock) { consumers = _consumers.ToList(); }

        foreach (var (consumerId, writer) in consumers)
        {
            if (!writer.TryWrite(chunk))
                _log.LogDebug("Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", consumerId, frameCount);
        }
    }

    /// <summary>
    /// Each channel's <see cref="WasapiCapture"/> independently activates its own WASAPI
    /// shared-mode client against the target device — unlike BASS, where a device had a single
    /// shared process-wide init/free lifecycle that other channels could accidentally be pulled
    /// out from under. Disposing this instance's capture/device objects cannot affect a sibling
    /// SoundcardCapture instance recording from the same physical device.
    /// </summary>
    private void DisposeCapture()
    {
        var capture = _capture;
        var mmDevice = _mmDevice;
        _capture = null;
        _mmDevice = null;

        if (capture != null)
        {
            try { capture.StopRecording(); } catch { /* already stopped */ }
            try { capture.Dispose(); } catch { /* already disposed */ }
        }
        try { mmDevice?.Dispose(); } catch { /* already disposed */ }
    }
}
