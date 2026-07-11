using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

public enum StreamStatus { Stopped, Connecting, Streaming, Reconnecting, Error }

public sealed class StreamCapture : FfmpegPipedCapture
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    private readonly string _url;
    private readonly string _streamType;
    private readonly bool _allowInvalidSsl;
    private readonly int _hlsBitrateIndex;

    protected override int GetSampleRate() => SampleRate;
    protected override int GetChannels() => Channels;

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
        : base(ffmpegPath, getReconnectDelaySeconds, getMaxReconnectAttempts, log, onReconnectExhausted, channelName)
    {
        _url = url;
        _streamType = streamType;
        _allowInvalidSsl = allowInvalidSsl;
        _hlsBitrateIndex = hlsBitrateIndex;
    }

    protected override string TargetDescription => _url;

    protected override string[] BuildArgs()
    {
        var ua = UserAgents.RandomDesktop();
        return BuildFfmpegArgs(_url, _streamType, _allowInvalidSsl, _hlsBitrateIndex, ua);
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
}
