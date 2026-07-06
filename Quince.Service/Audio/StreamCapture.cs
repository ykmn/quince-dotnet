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
    private readonly int _reconnectDelaySeconds;
    private readonly ILogger _log;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

    private volatile StreamStatus _status = StreamStatus.Stopped;
    private volatile int _reconnectAttempt;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public StreamCapture(string ffmpegPath, string url, string streamType, bool allowInvalidSsl,
        int hlsBitrateIndex, int reconnectDelaySeconds, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _url = url;
        _streamType = streamType;
        _allowInvalidSsl = allowInvalidSsl;
        _hlsBitrateIndex = hlsBitrateIndex;
        _reconnectDelaySeconds = Math.Max(1, reconnectDelaySeconds);
        _log = log;
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
        if (allowInvalidSsl) args.AddRange(new[] { "-tls_verify", "0" });
        args.AddRange(new[] { "-user_agent", userAgent });

        var isHls = streamType == "hls";
        if (isHls) args.AddRange(new[] { "-allowed_extensions", "ALL" });

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
                _status = StreamStatus.Streaming;
                if (_reconnectAttempt > 0)
                    _log.LogInformation("Переподключение к {Url} выполнено", _url);
                _reconnectAttempt = 0;

                var stderrDrainTask = DrainStderrAsync(process, ct);
                await ReadLoopAsync(process, ct);
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
            _status = StreamStatus.Reconnecting;
            _log.LogWarning("Поток отключён. Попытка переподключения {Attempt} через {Delay}с", _reconnectAttempt, _reconnectDelaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(_reconnectDelaySeconds), ct); }
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
                    _log.LogDebug("Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", consumerId, nFrames);
            }
        }

        if (process.HasExited && process.ExitCode != 0)
            _log.LogWarning("FFmpeg завершился с кодом {Code}", process.ExitCode);
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            var reader = process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _log.LogDebug("ffmpeg stderr: {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ошибка чтения stderr ffmpeg");
        }
    }
}
