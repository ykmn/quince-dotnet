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
        // ASP.NET Core deliberately does NOT percent-decode "%2F"/"%5C" when binding a route value
        // (confirmed empirically 2026-07-14 via a temporary debug-echo endpoint) — a guard against
        // path-traversal-style segment-count ambiguity for routes that build filesystem paths from
        // route values. This route never touches the filesystem with channelName (only a dictionary
        // lookup below), so there's no such risk here, and leaving "%2F" undecoded is exactly why
        // channel names containing "/" (this app's own "format/bitrate" naming convention, e.g.
        // "Studio21 Y401 mp3/96k") never matched any running engine: the value reaching this method
        // was literally "...mp3%2F96k", not "...mp3/96k". Un-escaping it here is what actually fixes
        // that — the earlier catch-all route change (docs/CHANGELOG.md 1.00.048) was chasing the
        // wrong mechanism (assumed the opposite: Kestrel over-decoding "%2F" into a segment-breaking
        // "/") and left this exact bug in place, which is why it kept failing after that fix too.
        channelName = Uri.UnescapeDataString(channelName);

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

        var ct = ctx.RequestAborted;

        // Diagnostic watchdog (docs/HISTORY.md — listen-in silently producing no audio for soundcard/
        // Livewire channels, root cause not found by static review of this pipeline): a real "capture
        // is healthy but this specific browser-playback consumer never receives anything" bug would
        // otherwise be invisible — the HTTP response just opens and idles forever with no error
        // anywhere. `+3` margin above the priming target covers ordinary scheduling jitter.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds + 3), ct); }
            catch (OperationCanceledException) { return; }
            if (!buffer.Primed)
                using (log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName }))
                    log.LogWarning(
                        "Потоковое прослушивание «{Channel}»: буфер не наполнился за {Timeout:F0}с — от захвата не пришло ни одного аудио-чанка для этого прослушивания, хотя канал числится запущенным",
                        channelName, delaySeconds + 3);
        });

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
                     "-hide_banner", "-loglevel", "error",
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

        using var streamScope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName });
        var stderrBuffer = new System.Text.StringBuilder();
        _ = DrainStderrAsync(proc, log, stderrBuffer);

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
            // An exit here (before the client itself disconnected, i.e. reached without an
            // OperationCanceledException above) means ffmpeg gave up on its own — e.g. an
            // unsupported/malformed input format for this channel's sample rate. Previously this
            // whole class of failure was invisible: stderr only ever went to LogDebug, below the
            // app's default INFO level, with no escalation on a bad exit (unlike AudioWriter/
            // FfmpegPipedCapture, which both buffer stderr and log it at Error on a crash).
            if (proc.HasExited && proc.ExitCode != 0)
            {
                var stderr = stderrBuffer.ToString();
                using (log.BeginScope(new Dictionary<string, object> { ["Channel"] = channelName }))
                    log.LogWarning("Потоковое прослушивание: ffmpeg завершился с кодом {Code} до отключения клиента. Stderr: {Stderr}",
                        proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? "(пусто)" : stderr.Trim());
            }
            try { if (!proc.HasExited) proc.Kill(true); } catch { /* already exited */ }
            try { await pumpIn; } catch { /* already logged above */ }
            proc.Dispose();
        }
    }

    private static async Task DrainStderrAsync(Process proc, ILogger log, System.Text.StringBuilder stderrBuffer)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                log.LogDebug("ffmpeg (потоковое прослушивание): {Line}", line);
                stderrBuffer.AppendLine(line);
            }
        }
        catch { /* process exited/killed while reading */ }
    }
}
