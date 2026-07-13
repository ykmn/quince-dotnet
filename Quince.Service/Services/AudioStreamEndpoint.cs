using System.ComponentModel;
using System.Diagnostics;
using Quince.Service.Audio;

namespace Quince.Service.Services;

/// <summary>
/// Serves a channel's live audio to the browser as a continuous MP3 stream, for the ▶ "listen to
/// channel" button (<see cref="AudioPlaybackService"/>). Subscribes to the channel's raw PCM the
/// same way <see cref="Audio.AudioWriter"/> does, but runs it through the same <see cref="PlayoutBuffer"/>
/// the level meter uses (docs/HISTORY.md #61), sized to the same source-aware delay
/// <see cref="Audio.ChannelEngine"/> resolved for that channel's meter (docs/HISTORY.md #126 — small
/// for continuous sources, measured-segment-based for HLS) before piping it through the bundled
/// ffmpeg to encode MP3 in real time and copying ffmpeg's stdout straight into the HTTP response body
/// — an &lt;audio&gt; element in the client plays it through the browser's/OS's current default
/// output device. The buffer means what's heard lags the real feed by that same delay (so audio and
/// indicator stay in sync with each other) but no longer audibly stutters on the same
/// producer-side gaps the meter used to visibly freeze on. No device selection: that needs a secure
/// context (HTTPS) for <c>HTMLMediaElement.setSinkId()</c>, which this app doesn't have yet.
/// </summary>
public static class AudioStreamEndpoint
{
    private const int FallbackSampleRate = 44100;
    private const int Channels = 2;

    public static async Task StreamAsync(string channelName, HttpContext ctx, AudioEngineManager engineManager, ILogger<AudioEngineManager> log)
    {
        var consumerId = $"browser-playback-{Guid.NewGuid():N}";
        var reader = engineManager.SubscribeAudio(channelName, consumerId);
        if (reader is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Must match the channel's actual capture rate (44100 for stream/soundcard, 48000 for
        // Livewire — LivewireCapture.SampleRate) rather than assuming a fixed rate: telling ffmpeg
        // the raw f32le bytes below are a different rate than they actually are doesn't resample
        // them, it just plays them at the wrong speed, audibly shifting pitch.
        var sampleRate = engineManager.GetSampleRate(channelName) ?? FallbackSampleRate;

        // Same delay ChannelEngine resolved for this channel's meter (source-aware — see
        // docs/HISTORY.md #126) so audio and the on-screen indicator stay in sync; falls back to the
        // app-wide non-HLS setting only if the channel somehow isn't in the running-engines map at
        // this exact moment (shouldn't normally happen: SubscribeAudio above already returned
        // non-null, implying it's running).
        var delaySeconds = engineManager.GetPlayoutBufferSeconds(channelName) ?? engineManager.PlayoutBufferSeconds;

        var buffer = new PlayoutBuffer(reader, sampleRate, log, channelName, delaySeconds);
        buffer.Start();

        ctx.Response.ContentType = "audio/mpeg";
        ctx.Response.Headers.CacheControl = "no-store";

        var psi = new ProcessStartInfo(engineManager.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-f", "f32le", "-ar", sampleRate.ToString(), "-ac", Channels.ToString(), "-i", "pipe:0",
                     "-f", "mp3", "-b:a", "128k", "pipe:1",
                 })
            psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            engineManager.UnsubscribeAudio(channelName, consumerId);
            using (log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName }))
                log.LogError(ex, "ffmpeg не найден по пути {Path} — не удалось начать потоковое прослушивание", engineManager.FfmpegPath);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var ct = ctx.RequestAborted;
        using var streamScope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName });
        _ = DrainStderrAsync(proc, log);

        var pumpIn = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in buffer.Reader.ReadAllAsync(ct))
                {
                    var bytes = new byte[chunk.Samples.Length * sizeof(float)];
                    Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, bytes.Length);
                    await proc.StandardInput.BaseStream.WriteAsync(bytes, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Ошибка передачи аудио в ffmpeg при потоковом прослушивании");
            }
            finally
            {
                try { proc.StandardInput.Close(); } catch { /* process may have already exited */ }
            }
        });

        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, ct);
        }
        catch (OperationCanceledException) { /* client disconnected/stopped playback — expected */ }
        finally
        {
            engineManager.UnsubscribeAudio(channelName, consumerId);
            buffer.Stop();
            try { if (!proc.HasExited) proc.Kill(true); } catch { /* already exited */ }
            try { await pumpIn; } catch { /* already logged above */ }
            proc.Dispose();
        }
    }

    private static async Task DrainStderrAsync(Process proc, ILogger log)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                log.LogDebug("ffmpeg (потоковое прослушивание): {Line}", line);
            }
        }
        catch { /* process exited/killed while reading */ }
    }
}
