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
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer" };
        if (allowInvalidSsl) args.AddRange(new[] { "-tls_verify", "0" });
        args.AddRange(new[] { "-user_agent", userAgent });

        var isHls = streamType == "hls";
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
            _log.LogInformation("[{Channel}] Подключение к {Url} (попытка {Attempt})", _channelName, _url, _reconnectAttempt);

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
                if (_reconnectAttempt > 0)
                    _log.LogInformation("[{Channel}] Переподключение к {Url} выполнено", _channelName, _url);
                _reconnectAttempt = 0;

                var stderrDrainTask = DrainStderrAsync(process, ct);
                await ReadLoopAsync(process, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "[{Channel}] Ошибка ffmpeg", _channelName);
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
                _log.LogError("[{Channel}] Превышен предел попыток переподключения ({Max}) — канал останавливается", _channelName, maxAttempts);
                if (_onReconnectExhausted != null) _ = Task.Run(_onReconnectExhausted);
                return;
            }

            _status = StreamStatus.Reconnecting;
            var delaySeconds = Math.Max(1, _getReconnectDelaySeconds());
            _log.LogWarning("[{Channel}] Поток отключён. Попытка переподключения {Attempt} через {Delay}с", _channelName, _reconnectAttempt, delaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _status = StreamStatus.Stopped;
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[ReadBytes];

        while (!ct.IsCancellationRequested)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
                if (n == 0) break;
                totalRead += n;
            }
            if (totalRead == 0) break; // EOF — process exited

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
                    _log.LogDebug("[{Channel}] Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", _channelName, consumerId, nFrames);
            }
        }

        if (process.HasExited && process.ExitCode != 0)
        {
            string stderr;
            lock (_stderrLock) { stderr = _stderrBuffer.ToString(); }
            if (string.IsNullOrWhiteSpace(stderr))
                _log.LogWarning("[{Channel}] FFmpeg завершился с кодом {Code}", _channelName, process.ExitCode);
            else
                _log.LogWarning("[{Channel}] FFmpeg завершился с кодом {Code}. Stderr: {Stderr}", _channelName, process.ExitCode, stderr.Trim());
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
                _log.LogDebug("[{Channel}] ffmpeg stderr: {Line}", _channelName, line);
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
            _log.LogDebug(ex, "[{Channel}] Ошибка чтения stderr ffmpeg", _channelName);
        }
    }
}
