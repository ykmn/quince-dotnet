using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

public enum StreamStatus { Stopped, Connecting, Streaming, Reconnecting, Error }

public sealed class StreamCapture : IAudioCapture
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    private const int BlockFrames = 4096;
    private const int BytesPerSample = 4;
    private static readonly int ReadBytes = BlockFrames * Channels * BytesPerSample;

    /// <summary>How long <see cref="ReadLoopAsync"/> tolerates ffmpeg producing zero stdout bytes
    /// before treating the process as stalled and forcing a reconnect. Guards against ffmpeg
    /// hanging on a stuck network read (most commonly seen on HLS, whose demuxer polls a live
    /// playlist and fetches segments — a stalled playlist/segment fetch can block ffmpeg
    /// indefinitely without it exiting or writing anything to stderr) — without this, the read loop
    /// would just await forever: no exception, no process exit, so the existing reconnect logic
    /// never triggers and level indicators silently freeze on their last value.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly string _ffmpegPath;
    private readonly string _url;
    private readonly string _streamType;
    private readonly bool _allowInvalidSsl;
    private readonly int _hlsBitrateIndex;
    private readonly Func<int> _getReconnectDelaySeconds;
    private readonly Func<int> _getMaxReconnectAttempts;
    private readonly Action? _onReconnectExhausted;
    private readonly ILogger _log;
    private readonly string _channelName;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

    private volatile StreamStatus _status = StreamStatus.Stopped;
    private volatile int _reconnectAttempt;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly System.Text.StringBuilder _stderrBuffer = new();
    private readonly object _stderrLock = new();

    /// <param name="getReconnectDelaySeconds">Read live at each retry (not cached at construction)
    /// so changing it in app settings applies immediately to already-running channels, same as
    /// <see cref="MetadataWriter"/>'s ad-keyword getter.</param>
    /// <param name="getMaxReconnectAttempts">0 = unlimited retries.</param>
    /// <param name="onReconnectExhausted">Fired once, off this instance's own loop thread (via a
    /// detached <see cref="Task.Run(Action)"/>), when the attempt budget runs out — lets the
    /// callback freely call back into <see cref="Stop"/> without deadlocking on this loop's own
    /// task.</param>
    public StreamCapture(string ffmpegPath, string url, string streamType, bool allowInvalidSsl,
        int hlsBitrateIndex, Func<int> getReconnectDelaySeconds, Func<int> getMaxReconnectAttempts,
        ILogger log, Action? onReconnectExhausted = null, string channelName = "")
    {
        _ffmpegPath = ffmpegPath;
        _url = url;
        _streamType = streamType;
        _allowInvalidSsl = allowInvalidSsl;
        _hlsBitrateIndex = hlsBitrateIndex;
        _getReconnectDelaySeconds = getReconnectDelaySeconds;
        _getMaxReconnectAttempts = getMaxReconnectAttempts;
        _onReconnectExhausted = onReconnectExhausted;
        _log = log;
        _channelName = channelName;
    }

    // Explicit interface implementation: the const fields above keep working for the existing
    // static usages (e.g. BuildFfmpegArgs), while IAudioCapture consumers see them as instance
    // properties (a const field and an instance property can't share a simple name in C#).
    int IAudioCapture.SampleRate => SampleRate;
    int IAudioCapture.Channels => Channels;

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
        var proc = _process;
        if (proc != null)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        _task = null;
        _process = null;
    }

    internal static string[] BuildFfmpegArgs(string url, string streamType, bool allowInvalidSsl, int hlsBitrateIndex, string userAgent)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        var isHls = streamType == "hls";

        // -fflags nobuffer minimizes latency by disabling ffmpeg's internal buffering — fine for
        // Icecast (one continuous byte stream, nothing to smooth over), but for HLS it also removes
        // any cushion against the natural once-per-segment-duration gap while ffmpeg waits for the
        // next live segment to be published, which showed up live as periodic ~1-2.5s audio gaps
        // tightly synchronized to each stream's segment duration (docs/HISTORY.md #56/#57) —
        // affecting HLS channels only, never Icecast. Tried -http_persistent 1 first (#56, assuming
        // a reconnect-per-segment cause) but that made the gaps WORSE (~4-4.5s) rather than better,
        // ruling out "fresh connection per fetch" as the mechanism and pointing at the inherent
        // segment-wait instead — reverted. This app is a 24/7 recorder, not a live-interactive
        // player, so trading a bit of added latency for smoother HLS output is an easy call.
        if (!isHls) args.AddRange(new[] { "-fflags", "nobuffer" });

        if (allowInvalidSsl) args.AddRange(new[] { "-tls_verify", "0" });
        args.AddRange(new[] { "-user_agent", userAgent });

        // Without -live_start_index -1, ffmpeg's HLS demuxer starts from the oldest segment still
        // in the live playlist window rather than the current live edge — for a typical few-segment
        // rolling window (segments a few seconds each) that means playback has to "catch up" through
        // everything already buffered before real-time audio arrives, unlike Icecast's single
        // continuous connection which has no such window to drain.
        if (isHls) args.AddRange(new[] { "-allowed_extensions", "ALL", "-live_start_index", "-1" });

        args.AddRange(new[] { "-i", url });

        if (isHls) args.AddRange(new[] { "-map", $"0:a:{hlsBitrateIndex}" });

        args.AddRange(new[]
        {
            "-vn",
            "-acodec", "pcm_f32le",
            "-ar", SampleRate.ToString(),
            "-ac", Channels.ToString(),
            "-f", "f32le",
            "pipe:1",
        });
        return args.ToArray();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _reconnectAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            _status = StreamStatus.Connecting;
            _log.LogInformation("Подключение к {Url} (попытка {Attempt})", _url, _reconnectAttempt);

            var ua = UserAgents.RandomDesktop();
            var args = BuildFfmpegArgs(_url, _streamType, _allowInvalidSsl, _hlsBitrateIndex, ua);

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo(_ffmpegPath)
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

                // _reconnectAttempt is NOT reset here — a bad/unreachable URL still lets the ffmpeg
                // *process* start fine (it only fails internally to open the input and exits with no
                // output); resetting on Process.Start alone wiped the counter every single loop,
                // before ReconnectMaxAttempts could ever be exceeded. It's reset in ReadLoopAsync
                // instead, the moment real audio bytes are actually confirmed flowing.
                var stderrDrainTask = DrainStderrAsync(process, ct);
                await ReadLoopAsync(process, ct);
            }
            catch (StreamStallException ex)
            {
                _log.LogWarning("Зависание ffmpeg: {Message} — перезапуск", ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Ошибка ffmpeg");
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
                _log.LogError("Превышен предел попыток переподключения ({Max}) — канал останавливается", maxAttempts);
                if (_onReconnectExhausted != null) _ = Task.Run(_onReconnectExhausted);
                return;
            }

            _status = StreamStatus.Reconnecting;
            var delaySeconds = Math.Max(1, _getReconnectDelaySeconds());
            _log.LogWarning("Поток отключён. Попытка переподключения {Attempt} через {Delay}с", _reconnectAttempt, delaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _status = StreamStatus.Stopped;
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[ReadBytes];
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
            // forgive earlier failed attempts, unlike the old reset-on-Process.Start behavior that
            // let a persistently bad URL loop forever regardless of ReconnectMaxAttempts.
            if (_reconnectAttempt > 0)
            {
                _log.LogInformation("Переподключение к {Url} выполнено", _url);
                _reconnectAttempt = 0;
            }

            var nSamples = totalRead / BytesPerSample;
            var nFrames = nSamples / Channels;
            if (nFrames == 0) continue;

            var samples = new float[nFrames * Channels];
            Buffer.BlockCopy(buffer, 0, samples, 0, samples.Length * sizeof(float));
            var chunk = new AudioChunk(samples, Channels);

            List<KeyValuePair<string, ChannelWriter<AudioChunk>>> consumers;
            lock (_lock) { consumers = _consumers.ToList(); }

            foreach (var (consumerId, writer) in consumers)
            {
                if (!writer.TryWrite(chunk))
                    _log.LogDebug("Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", consumerId, nFrames);
            }
        }

        if (process.HasExited && process.ExitCode != 0)
        {
            string stderr;
            lock (_stderrLock) { stderr = _stderrBuffer.ToString(); }
            if (string.IsNullOrWhiteSpace(stderr))
                _log.LogWarning("FFmpeg завершился с кодом {Code}", process.ExitCode);
            else
                _log.LogWarning("FFmpeg завершился с кодом {Code}. Stderr: {Stderr}", process.ExitCode, stderr.Trim());
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
                _log.LogDebug("ffmpeg stderr: {Line}", line);
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
            _log.LogDebug(ex, "Ошибка чтения stderr ffmpeg");
        }
    }

    /// <summary>Thrown by <see cref="ReadLoopAsync"/> when ffmpeg stops producing stdout bytes for
    /// longer than <see cref="StallTimeout"/> without exiting — treated like any other capture
    /// failure by <see cref="RunLoopAsync"/>'s reconnect logic.</summary>
    private sealed class StreamStallException(string message) : Exception(message);
}
