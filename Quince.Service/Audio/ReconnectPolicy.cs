namespace Quince.Service.Audio;

/// <summary>
/// Shared reconnect pacing for the capture backends. A fixed pause between attempts used to hammer
/// a dead source forever (6666 attempts and a 40 MB log in one day when a CDN returned 404 for 22
/// hours): the pause now doubles per consecutive failed attempt up to <see cref="MaxDelaySeconds"/>,
/// and repeated per-attempt log lines are thinned out after the first few.
/// </summary>
public static class ReconnectPolicy
{
    /// <summary>Upper bound of the exponential pause. A configured base delay above this is kept as-is.</summary>
    public const int MaxDelaySeconds = 60;

    /// <summary>How many consecutive "ffmpeg opened the input but found no stream" exits are retried
    /// almost immediately (<see cref="QuickRetryDelaySeconds"/>, attempt counter untouched) before
    /// falling back to the normal backoff.</summary>
    public const int MaxQuickRetries = 5;
    public const int QuickRetryDelaySeconds = 1;

    /// <summary>Pause before reconnect attempt number <paramref name="attempt"/> (1-based):
    /// base, 2×base, 4×base … capped at <see cref="MaxDelaySeconds"/> (or the base itself if larger).</summary>
    public static int DelaySeconds(int baseDelaySeconds, int attempt)
    {
        var baseDelay = Math.Max(1, baseDelaySeconds);
        var cap = Math.Max(baseDelay, MaxDelaySeconds);
        var shift = Math.Clamp(attempt - 1, 0, 16);
        var delay = (long)baseDelay << shift;
        return (int)Math.Min(delay, cap);
    }

    /// <summary>Whether a per-attempt log line should be written: always for the first three
    /// attempts, then every 10th. Keeps a long outage readable without hiding that it continues.</summary>
    public static bool ShouldLogAttempt(int attempt) => attempt <= 3 || attempt % 10 == 0;
}
