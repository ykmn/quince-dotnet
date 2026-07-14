using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>
/// Shared ffmpeg-subprocess plumbing for capture backends that receive audio as a raw <c>f32le</c>
/// stream piped off ffmpeg's stdout: process spawn/kill, stdout read loop with a stall watchdog,
/// stderr draining, the Connecting/Streaming/Reconnecting/Error status machine, and per-consumer
/// fan-out of <see cref="AudioChunk"/>s. Extracted from what was originally all one class
/// (<see cref="StreamCapture"/>) once <see cref="LivewireCapture"/> needed the identical process-
/// lifecycle/reconnect logic for a different ffmpeg command line (an SDP file instead of a URL) —
/// re-deriving and separately hardening that logic a second time wasn't worth the risk given how
/// many rounds of real-world tuning this exact code has already been through (docs/HISTORY.md
/// #36/#52-61: thread-pool sizing, UI dispatch coalescing, HLS buffering, the jitter/playout
/// buffer — none of which live here, but the stall-watchdog/reconnect scaffolding they all sit on
/// does). Subclasses only decide WHAT to run (<see cref="BuildArgs"/>) and WHAT to call it in logs
/// (<see cref="TargetDescription"/>); everything about running and babysitting the process is here.
/// </summary>
public abstract class FfmpegPipedCapture : IAudioCapture
{
    private const int BlockFrames = 4096;
    private const int BytesPerSample = 4;

    /// <summary>How long the read loop tolerates ffmpeg producing zero stdout bytes before treating
    /// the process as stalled and forcing a reconnect — see <see cref="StreamCapture"/>'s original
    /// doc comment for why this exists (a stuck network read that never makes ffmpeg exit or write
    /// to stderr would otherwise hang the read loop forever, silently freezing indicators).</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    protected readonly string FfmpegPath;
    protected readonly ILogger Log;
    protected readonly string ChannelName;

    private readonly Func<int> _getReconnectDelaySeconds;
    private readonly Func<int> _getMaxReconnectAttempts;
    private readonly Action? _onReconnectExhausted;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

    private volatile StreamStatus _status = StreamStatus.Stopped;
    private volatile int _reconnectAttempt;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly System.Text.StringBuilder _stderrBuffer = new();
    private readonly object _stderrLock = new();

    protected FfmpegPipedCapture(string ffmpegPath, Func<int> getReconnectDelaySeconds, Func<int> getMaxReconnectAttempts,
        ILogger log, Action? onReconnectExhausted, string channelName)
    {
        FfmpegPath = ffmpegPath;
        _getReconnectDelaySeconds = getReconnectDelaySeconds;
        _getMaxReconnectAttempts = getMaxReconnectAttempts;
        _onReconnectExhausted = onReconnectExhausted;
        Log = log;
        ChannelName = channelName;
    }

    // Explicit interface implementation, delegating to differently-named abstract members: a
    // subclass typically exposes its native rate/channels as a `public const int SampleRate/Channels`
    // (referenced by its own static ffmpeg-arg builder, e.g. StreamCapture.BuildFfmpegArgs), which
    // can't share a name with a normal abstract instance property — this is the same trick
    // StreamCapture used pre-refactor, just moved up to the base class.
    int IAudioCapture.SampleRate => GetSampleRate();
    int IAudioCapture.Channels => GetChannels();
    protected abstract int GetSampleRate();
    protected abstract int GetChannels();

    public StreamStatus Status => _status;
    public int ReconnectAttempt => _reconnectAttempt;
    public int? ProcessId => _process?.Id;

    /// <summary>Builds this attempt's ffmpeg command-line arguments (last element is always the
    /// output, "pipe:1" for f32le). Called fresh at the start of every connection attempt, so a
    /// subclass that needs a temp file (e.g. <see cref="LivewireCapture"/>'s SDP) can (re)write it
    /// here too.</summary>
    protected abstract string[] BuildArgs();

    /// <summary>Short human-readable description of what's being connected to, used only in log
    /// messages ("Подключение к {TargetDescription}...").</summary>
    protected abstract string TargetDescription { get; }

    /// <summary>Called once from <see cref="Stop"/>, after the process is killed — a hook for
    /// subclass cleanup (e.g. deleting a temp SDP file). No-op by default.</summary>
    protected virtual void OnStopped() { }

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
        var proc = _process;
        if (proc != null)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        _task = null;
        _process = null;
        OnStopped();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _reconnectAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            _status = StreamStatus.Connecting;
            Log.LogInformation("Подключение к {Target} (попытка {Attempt})", TargetDescription, _reconnectAttempt);

            Process? process = null;
            try
            {
                var args = BuildArgs();
                Log.LogDebug("ffmpeg аргументы: {Args}", string.Join(" ", args));
                var psi = new ProcessStartInfo(FfmpegPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                _process = process;
                lock (_stderrLock) { _stderrBuffer.Clear(); }
                _status = StreamStatus.Streaming;

                // _reconnectAttempt is NOT reset here — a bad/unreachable target still lets the
                // ffmpeg *process* start fine (it only fails internally to open the input and exits
                // with no output); it's reset in ReadLoopAsync instead, the moment real audio bytes
                // are actually confirmed flowing.
                var stderrDrainTask = DrainStderrAsync(process, ct);
                await ReadLoopAsync(process, ct);
            }
            catch (StreamStallException ex)
            {
                Log.LogWarning("Зависание ffmpeg: {Message} — перезапуск", ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogError(ex, "Ошибка ffmpeg");
            }
            finally
            {
                _process = null;
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                }
            }

            if (ct.IsCancellationRequested) break;

            _reconnectAttempt++;
            var maxAttempts = _getMaxReconnectAttempts();
            if (maxAttempts > 0 && _reconnectAttempt > maxAttempts)
            {
                _status = StreamStatus.Error;
                Log.LogError("Превышен предел попыток переподключения ({Max}) — канал останавливается", maxAttempts);
                if (_onReconnectExhausted != null) _ = Task.Run(_onReconnectExhausted);
                return;
            }

            _status = StreamStatus.Reconnecting;
            var delaySeconds = Math.Max(1, _getReconnectDelaySeconds());
            Log.LogWarning("Поток отключён. Попытка переподключения {Attempt} через {Delay}с", _reconnectAttempt, delaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _status = StreamStatus.Stopped;
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        var channels = GetChannels();
        var readBytes = BlockFrames * channels * BytesPerSample;
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[readBytes];
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (!ct.IsCancellationRequested)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int n;
                try
                {
                    stallCts.CancelAfter(StallTimeout);
                    n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), stallCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new StreamStallException($"ffmpeg не отдал ни байта дольше {StallTimeout.TotalSeconds:F0}с");
                }
                if (n == 0) break;
                totalRead += n;
            }
            if (totalRead == 0) break; // EOF — process exited

            // First real bytes actually confirm the connection works — only here is it safe to
            // forgive earlier failed attempts, unlike resetting on Process.Start alone, which would
            // let a persistently bad target loop forever regardless of ReconnectMaxAttempts.
            if (_reconnectAttempt > 0)
            {
                Log.LogInformation("Переподключение к {Target} выполнено", TargetDescription);
                _reconnectAttempt = 0;
            }

            var nSamples = totalRead / BytesPerSample;
            var nFrames = nSamples / channels;
            if (nFrames == 0) continue;

            var samples = new float[nFrames * channels];
            Buffer.BlockCopy(buffer, 0, samples, 0, samples.Length * sizeof(float));
            var chunk = new AudioChunk(samples, channels);

            List<KeyValuePair<string, ChannelWriter<AudioChunk>>> consumers;
            lock (_lock) { consumers = _consumers.ToList(); }

            foreach (var (consumerId, writer) in consumers)
            {
                if (!writer.TryWrite(chunk))
                    Log.LogDebug("Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", consumerId, nFrames);
            }
        }

        if (process.HasExited && process.ExitCode != 0)
        {
            string stderr;
            lock (_stderrLock) { stderr = _stderrBuffer.ToString(); }
            if (string.IsNullOrWhiteSpace(stderr))
                Log.LogWarning("FFmpeg завершился с кодом {Code}", process.ExitCode);
            else
                Log.LogWarning("FFmpeg завершился с кодом {Code}. Stderr: {Stderr}", process.ExitCode, stderr.Trim());
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            var reader = process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Log.LogDebug("ffmpeg stderr: {Line}", line);
                lock (_stderrLock)
                {
                    _stderrBuffer.AppendLine(line);
                    // Bound growth in case of a very chatty/long-running process before it crashes.
                    if (_stderrBuffer.Length > 16_384)
                        _stderrBuffer.Remove(0, _stderrBuffer.Length - 16_384);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Ошибка чтения stderr ffmpeg");
        }
    }

    /// <summary>Thrown by <see cref="ReadLoopAsync"/> when ffmpeg stops producing stdout bytes for
    /// longer than <see cref="StallTimeout"/> without exiting — treated like any other capture
    /// failure by <see cref="RunLoopAsync"/>'s reconnect logic.</summary>
    private sealed class StreamStallException(string message) : Exception(message);
}
