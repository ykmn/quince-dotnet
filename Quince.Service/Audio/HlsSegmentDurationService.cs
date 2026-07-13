using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>
/// Measures an HLS channel's real segment cadence (its playlist's <c>#EXT-X-TARGETDURATION</c>) so
/// its <see cref="PlayoutBuffer"/> can be sized to what that specific channel actually needs, instead
/// of one blanket worst-case constant applied to every HLS channel regardless of whether its real
/// cadence is ~2s or ~10s (docs/HISTORY.md #36/#52-#61 — the periodic gap this buffers against is an
/// inherent property of live HLS segment delivery, confirmed to never occur on Icecast/soundcard/
/// Livewire's continuous streams).
///
/// Deliberately does NOT resize an already-running <see cref="PlayoutBuffer"/> — a measurement only
/// takes effect the next time the owning channel's <see cref="ChannelEngine"/> restarts (config edit
/// via <see cref="ChannelEngine.PipelineChanged"/>, manual stop/start, or app restart), which this
/// app already does reasonably often. Teaching a live, already-primed buffer to safely grow/shrink
/// mid-flight (without releasing stale data early or deadlocking against an already-exceeded
/// threshold) is meaningfully riskier than that small added delay.
///
/// <see cref="RequestRefresh"/> is fire-and-forget and never throws, never blocks the caller, and
/// never regresses: any failure (unreachable playlist, malformed text, timeout, no
/// <c>TARGETDURATION</c> found) just leaves the cache untouched, so callers fall back to
/// <see cref="PlayoutBuffer.DefaultTargetDelaySeconds"/> — today's already-proven-safe constant —
/// until a measurement succeeds.
/// </summary>
public sealed class HlsSegmentDurationService
{
    /// <summary>Added on top of the measured segment duration — matches this project's own
    /// known-good precedent exactly: the current flat constant (12s) was arrived at as roughly the
    /// worst observed real segment duration (~10s) plus ~2s of margin (docs/HISTORY.md #61).</summary>
    public const double MarginSeconds = 2.0;

    /// <summary>Floor so an unusually short-segment stream (e.g. ~1s) still gets a sane minimum
    /// cushion, not a buffer barely bigger than a single segment.</summary>
    public const double MinDelaySeconds = 4.0;

    /// <summary>Ceiling — a sanity bound against a bogus/misread value, not a cap at today's 12s: a
    /// channel with genuinely long real segments deserves a bigger buffer than before, not one
    /// silently under-sized to match the old blanket constant.</summary>
    public const double MaxDelaySeconds = 30.0;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, double> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly ILogger<HlsSegmentDurationService> _log;

    public HlsSegmentDurationService(ILogger<HlsSegmentDurationService> log)
    {
        _log = log;
    }

    /// <summary>The last successfully measured (margined, clamped) delay for this exact playlist
    /// URL, or null if never measured yet (cold start, or every attempt so far has failed). Purely
    /// synchronous — never does I/O.</summary>
    public double? TryGetCachedDelaySeconds(string playlistUrl) =>
        !string.IsNullOrEmpty(playlistUrl) && _cache.TryGetValue(playlistUrl, out var seconds) ? seconds : null;

    /// <summary>Kicks off a background measurement for <paramref name="playlistUrl"/> if one isn't
    /// already in flight for that exact URL (de-dupes overlapping calls — e.g. several restarts of
    /// the same channel in quick succession). Fire-and-forget: the caller does not wait on this and
    /// should already be using <see cref="TryGetCachedDelaySeconds"/>'s current value (or its own
    /// fallback) for the run that triggered this call.</summary>
    public void RequestRefresh(string playlistUrl, bool allowInvalidSsl, int hlsBitrateIndex, string channelName)
    {
        if (string.IsNullOrEmpty(playlistUrl)) return;
        if (!_inFlight.TryAdd(playlistUrl, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = MetadataHttp.CreateClient(allowInvalidSsl, HttpTimeout);
                using var cts = new CancellationTokenSource(FetchTimeout);
                var duration = await FetchTargetDurationAsync(playlistUrl, hlsBitrateIndex, client, cts.Token);
                if (duration is > 0)
                {
                    var delay = ComputeDelaySeconds(duration.Value);
                    _cache[playlistUrl] = delay;
                    _log.LogInformation(
                        "HLS: сегмент {Duration:F1}с ({Channel}) — буфер индикаторов {Delay:F1}с при следующем запуске канала",
                        duration.Value, channelName, delay);
                }
                else
                {
                    _log.LogInformation(
                        "HLS: не удалось определить длительность сегмента для {Channel} — буфер индикаторов останется прежним",
                        channelName);
                }
            }
            catch (Exception ex)
            {
                _log.LogInformation(ex,
                    "HLS: ошибка получения длительности сегмента для {Channel} — буфер индикаторов останется прежним",
                    channelName);
            }
            finally
            {
                _inFlight.TryRemove(playlistUrl, out _);
            }
        });
    }

    /// <summary>Fetches <paramref name="playlistUrl"/>; if it's a master playlist (rendition
    /// variants, no segments of its own), follows one representative variant — the one
    /// <paramref name="hlsBitrateIndex"/> selects, same as <see cref="StreamCapture"/>'s own
    /// <c>-map 0:a:{index}</c> — and measures that instead. One hop only: if the "variant" turns out
    /// to be another master playlist, that's treated as malformed (real HLS never nests masters).</summary>
    private static async Task<double?> FetchTargetDurationAsync(string playlistUrl, int hlsBitrateIndex, HttpClient client, CancellationToken ct)
    {
        // One User-Agent for both requests in this chain — confirmed live against a real station
        // (hostingradio.ru) that its master-playlist response mints a session id (its own
        // "?hlssid=..." query param on each variant URI) tied to the User-Agent that requested it: a
        // second request for the same session with a DIFFERENT randomly-picked UA gets rejected with
        // "user info mismatch" (400). Picking independently per call (the naive approach) intermittently
        // 400s on exactly this two-hop path.
        var userAgent = UserAgents.RandomDesktop();

        var text = await FetchTextAsync(client, playlistUrl, userAgent, ct);
        if (!HlsPlaylistParser.IsMasterPlaylist(text))
            return HlsPlaylistParser.ParseTargetDurationSeconds(text);

        var variants = HlsPlaylistParser.ParseVariantUris(text, new Uri(playlistUrl));
        if (variants.Count == 0) return null;

        var index = Math.Clamp(hlsBitrateIndex, 0, variants.Count - 1);
        var variantText = await FetchTextAsync(client, variants[index].ToString(), userAgent, ct);
        return HlsPlaylistParser.IsMasterPlaylist(variantText) ? null : HlsPlaylistParser.ParseTargetDurationSeconds(variantText);
    }

    private static async Task<string> FetchTextAsync(HttpClient client, string url, string userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Adds <see cref="MarginSeconds"/> then clamps to <c>[MinDelaySeconds, MaxDelaySeconds]</c>
    /// — split out as its own pure function purely so the margin/clamp math is unit-testable without
    /// any I/O.</summary>
    internal static double ComputeDelaySeconds(double targetDurationSeconds) =>
        Math.Clamp(targetDurationSeconds + MarginSeconds, MinDelaySeconds, MaxDelaySeconds);
}
