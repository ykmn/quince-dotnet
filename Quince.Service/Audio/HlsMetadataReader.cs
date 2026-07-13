using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>
/// Reads "now playing" metadata for an HLS stream. Direct port of the legacy Python
/// implementation's <c>HlsMetadataReader</c> (<c>src/audio/metadata_hls.py</c>):
/// <list type="number">
/// <item>Try to discover a JSON metadata endpoint from the playlist URL and poll it every 5s.</item>
/// <item>If no JSON endpoint responds, fall back to a single ffprobe ID3-tag probe of the stream
/// itself (not retried).</item>
/// </list>
/// </summary>
public sealed class HlsMetadataReader : IMetadataReader
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // Not const/readonly so tests can shrink these instead of waiting through the real cadence.
    internal static int DiscoveryAttempts = 3;
    internal static TimeSpan DiscoveryRetryDelay = TimeSpan.FromSeconds(3);
    internal static TimeSpan BackgroundResolveInterval = TimeSpan.FromMinutes(2);

    private static readonly string[] JsonTitleKeys = { "title", "name", "song" };
    private static readonly string[] FlatStringKeys = { "now_playing", "current", "stream_title" };

    private readonly string _playlistUrl;
    private readonly bool _allowInvalidSsl;
    private readonly Action<MetadataEvent>? _onMetadata;
    private readonly string _channelName;
    private readonly string _ffprobePath;
    private readonly ILogger _log;
    private readonly string? _knownMetadataUrl;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private string _lastRaw = "";
    private volatile bool _hasMetadata;
    private string? _metadataUrl;

    /// <param name="knownMetadataUrl">A JSON endpoint already confirmed by a previous "Определить
    /// наличие метаданных" detection (<see cref="Configuration.SourceConfig.MetadataUrl"/>), tried
    /// directly before falling back to re-deriving candidates from <paramref name="playlistUrl"/>.
    /// Needed because the JSON endpoint's host/path can differ from the playlist's (e.g. a station
    /// serving playlists under one stream id but metadata under another) — re-deriving from the
    /// playlist URL alone would never find it.</param>
    public HlsMetadataReader(string playlistUrl, bool allowInvalidSsl, Action<MetadataEvent>? onMetadata,
        string channelName, string ffprobePath, ILogger log, string? knownMetadataUrl = null)
    {
        _playlistUrl = playlistUrl;
        _allowInvalidSsl = allowInvalidSsl;
        _onMetadata = onMetadata;
        _channelName = channelName;
        _ffprobePath = ffprobePath;
        _log = log;
        _knownMetadataUrl = knownMetadataUrl;
    }

    public bool HasMetadata => _hasMetadata;
    public bool IsRunning => _task is { IsCompleted: false };
    /// <summary>The discovered JSON endpoint URL, or null if none was found (or not yet tried).</summary>
    public string? MetadataUrl => _metadataUrl;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(15)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _log.BeginScope(new Dictionary<string, object> { ["Channel"] = _channelName });

        if (!string.IsNullOrEmpty(_knownMetadataUrl) && await TryKnownUrlWithRetriesAsync(ct))
        {
            _hasMetadata = true;
            _log.LogInformation("Метаданные HLS: JSON endpoint {Url}", _metadataUrl);
            await JsonPollLoopAsync(ct);
            return;
        }

        // A single failed discovery attempt used to permanently fall back to the one-shot ID3
        // probe for the rest of the channel's session, even though the JSON endpoint genuinely
        // existed — e.g. a transient DNS/network hiccup right at app startup (several channels
        // opening connections at once) was enough to mask working metadata for the whole run.
        // Retry a few times before giving up on JSON discovery.
        _metadataUrl = await TryDiscoveryWithRetriesAsync(ct);
        if (ct.IsCancellationRequested) return;

        if (_metadataUrl != null)
        {
            _hasMetadata = true;
            _log.LogInformation("Метаданные HLS: JSON endpoint {Url}", _metadataUrl);
            await JsonPollLoopAsync(ct);
            return;
        }

        _log.LogInformation("JSON метаданные не найдены, пробуем ID3 через FFmpeg");

        if (!File.Exists(_ffprobePath))
        {
            // Expected on machines where ffprobe.exe hasn't been placed next to ffmpeg.exe in
            // tools/ — it isn't bundled (same licensing-review status as bass.dll, see README).
            // Not an error: this is only the last-resort HLS metadata fallback.
            _log.LogInformation("ffprobe.exe не найден ({Path}) — резервный разбор ID3-тегов недоступен, метаданные не обнаружены", _ffprobePath);
        }
        else
        {
            try
            {
                var result = await ProbeId3Async(ct);
                if (result != null)
                {
                    _hasMetadata = true;
                    FireIfChanged(result.Value.Artist, result.Value.Title);
                }
                else
                {
                    _log.LogInformation("Метаданные не обнаружены");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Ошибка FFmpeg ID3 probe");
            }
        }

        // Don't let this reader's task die here. JSON discovery (and, if configured, the known
        // endpoint) may simply be down right now — e.g. a reconnect storm at startup, or the
        // metadata host being briefly unreachable — and may become reachable again later in this
        // channel's (potentially many-hour) session. Keep periodically re-resolving in the
        // background so the reader self-heals into full JSON polling without a manual config
        // reload. The one-shot ID3 probe above is intentionally NOT retried here — only JSON
        // resolution is.
        await BackgroundResolveLoopAsync(ct);
    }

    private async Task<bool> TryKnownUrlWithRetriesAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= DiscoveryAttempts; attempt++)
        {
            if (await TryKnownUrlAsync(ct)) return true;
            if (attempt < DiscoveryAttempts)
            {
                try { await Task.Delay(DiscoveryRetryDelay, ct); }
                catch (OperationCanceledException) { return false; }
            }
        }
        return false;
    }

    private async Task<string?> TryDiscoveryWithRetriesAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= DiscoveryAttempts; attempt++)
        {
            var url = await DiscoverMetadataUrlAsync(_playlistUrl, _allowInvalidSsl, DiscoveryTimeout);
            if (url != null) return url;
            if (attempt < DiscoveryAttempts)
            {
                try { await Task.Delay(DiscoveryRetryDelay, ct); }
                catch (OperationCanceledException) { return null; }
            }
        }
        return null;
    }

    /// <summary>Runs once known-URL retries + discovery retries + the one-shot ID3 fallback have
    /// all failed at startup. Keeps re-attempting JSON resolution (known URL, then discovery) on a
    /// slow cadence for the rest of the reader's life so a channel like one whose metadata host
    /// differs from its playlist host (see <see cref="_knownMetadataUrl"/>'s doc) self-heals if the
    /// endpoint was only transiently unreachable, instead of staying without live metadata until a
    /// manual channel config reload recreates this reader from scratch.</summary>
    private async Task BackgroundResolveLoopAsync(CancellationToken ct)
    {
        _log.LogInformation(
            "Метаданные не обнаружены при запуске — продолжаем периодические попытки поиска JSON в фоне (каждые {Interval} мин)",
            BackgroundResolveInterval.TotalMinutes);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(BackgroundResolveInterval, ct); }
            catch (OperationCanceledException) { return; }

            if (!string.IsNullOrEmpty(_knownMetadataUrl) && await TryKnownUrlAsync(ct))
            {
                _hasMetadata = true;
                _log.LogInformation("Метаданные HLS восстановлены (фоновая попытка): JSON endpoint {Url}", _metadataUrl);
                await JsonPollLoopAsync(ct);
                return;
            }

            var url = await DiscoverMetadataUrlAsync(_playlistUrl, _allowInvalidSsl, DiscoveryTimeout);
            if (url != null)
            {
                _metadataUrl = url;
                _hasMetadata = true;
                _log.LogInformation("Метаданные HLS восстановлены (фоновая попытка): JSON endpoint {Url}", _metadataUrl);
                await JsonPollLoopAsync(ct);
                return;
            }

            _log.LogDebug("Фоновая попытка поиска JSON метаданных снова не удалась, следующая попытка через {Interval} мин",
                BackgroundResolveInterval.TotalMinutes);
        }
    }

    /// <summary>Tries <see cref="_knownMetadataUrl"/> directly (a single fetch). On success, sets
    /// <see cref="_metadataUrl"/> so <see cref="JsonPollLoopAsync"/> keeps polling it. Returns
    /// false on any failure (unreachable, malformed URL, or a response with no recognizable
    /// title) so the caller can fall back to fresh discovery.</summary>
    private async Task<bool> TryKnownUrlAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DiscoveryTimeout);
        if (!await ValidateUrlAsync(_knownMetadataUrl!, _allowInvalidSsl, cts.Token)) return false;
        _metadataUrl = _knownMetadataUrl;
        return true;
    }

    /// <summary>Fetches <paramref name="url"/> once and reports whether it currently yields a
    /// recognizable title — used both by the runtime reader (to trust an already-known-good
    /// endpoint before re-deriving candidates) and by <see cref="Services.MetadataDetectionService"/>
    /// (so the "Определить" button doesn't report "not found" for a station whose saved endpoint
    /// still works, just because it lives under a different path than the current playlist URL).</summary>
    internal static async Task<bool> ValidateUrlAsync(string url, bool allowInvalidSsl, CancellationToken ct)
    {
        try
        {
            var data = await FetchJsonAsync(url, allowInvalidSsl, ct);
            return ExtractFromJson(data) != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task JsonPollLoopAsync(CancellationToken ct)
    {
        // One HttpClient/HttpClientHandler for the whole polling session (this loop normally runs
        // for the channel's entire lifetime, polling every PollInterval) instead of one per poll —
        // a fresh HttpClientHandler per request means a fresh connection pool and a full TLS
        // handshake from scratch every time (no session/connection reuse), which under several
        // channels polling on roughly the same 5s cadence at once was observed to stall unrelated
        // channels' audio capture for 1-2+ seconds (see docs/HISTORY.md #54/#55).
        using var client = MetadataHttp.CreateClient(_allowInvalidSsl, TimeSpan.FromSeconds(5));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var data = await FetchJsonAsync(client, _metadataUrl!, ct);
                var result = ExtractFromJson(data);
                if (result != null)
                    FireIfChanged(result.Value.Artist, result.Value.Title);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Ошибка получения метаданных JSON");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void FireIfChanged(string artist, string title)
    {
        var raw = string.IsNullOrEmpty(artist) ? title : $"{artist} - {title}";
        if (raw == _lastRaw) return;
        _lastRaw = raw;
        try { _onMetadata?.Invoke(new MetadataEvent(raw, artist, title, DateTimeOffset.Now)); }
        catch (Exception ex) { _log.LogError(ex, "on_metadata callback raised"); }
    }

    private static async Task<JsonElement> FetchJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgents.RandomDesktop());
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>One-shot fetch (discovery/validation — not the hot polling path) that owns its own
    /// short-lived client.</summary>
    private static async Task<JsonElement> FetchJsonAsync(string url, bool allowInvalidSsl, CancellationToken ct)
    {
        using var client = MetadataHttp.CreateClient(allowInvalidSsl, TimeSpan.FromSeconds(5));
        return await FetchJsonAsync(client, url, ct);
    }

    /// <summary>
    /// Tries three candidate JSON metadata URLs derived from the playlist URL's own directory,
    /// same priority order as the legacy port: <c>metadata.json?format=fmgid&amp;subformat=small</c>,
    /// <c>metadata.json</c> in the same directory, then <c>/metadata.json</c> at the host root.
    /// Returns the first URL whose response contains a recognizable title, or null if none do.
    /// </summary>
    internal static async Task<string?> DiscoverMetadataUrlAsync(string playlistUrl, bool allowInvalidSsl, TimeSpan timeout)
    {
        List<string> candidates;
        try
        {
            // BuildCandidateUrls is an iterator method — Uri parsing inside it (which can throw
            // UriFormatException for a malformed playlist URL) wouldn't otherwise run until the
            // foreach below pulls the first item, which is BEFORE that loop's own try/catch takes
            // effect. Materialize eagerly so a bad URL is just "nothing found", not an escaped
            // exception from the very first MoveNext().
            candidates = BuildCandidateUrls(playlistUrl).ToList();
        }
        catch
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                var data = await FetchJsonAsync(candidate, allowInvalidSsl, cts.Token);
                if (ExtractFromJson(data) != null)
                    return candidate;
            }
            catch
            {
                // try the next candidate
            }
        }
        return null;
    }

    internal static IEnumerable<string> BuildCandidateUrls(string playlistUrl)
    {
        var uri = new Uri(playlistUrl);
        var baseUrl = $"{uri.Scheme}://{uri.Authority}";
        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var pathDir = lastSlash >= 0 ? path[..lastSlash] : "";

        yield return $"{baseUrl}{pathDir}/metadata.json?format=fmgid&subformat=small";
        yield return $"{baseUrl}{pathDir}/metadata.json";
        yield return $"{baseUrl}/metadata.json";
    }

    /// <summary>
    /// Extracts (artist, title) from various JSON "now playing" formats, same priority order as
    /// the legacy port's <c>_extract_from_json</c>: an <c>fmgid</c> sub-object, then flat
    /// artist/title-ish top-level fields, then a flat "Artist - Title" string field.
    /// </summary>
    internal static (string Artist, string Title)? ExtractFromJson(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        if (data.TryGetProperty("fmgid", out var fmgid) && fmgid.ValueKind == JsonValueKind.Object)
        {
            var artist = GetString(fmgid, "artist") ?? "";
            var title = JsonTitleKeys.Select(k => GetString(fmgid, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
            if (!string.IsNullOrEmpty(title)) return (artist, title);
        }

        var topArtist = GetString(data, "artist") ?? "";
        var topTitle = JsonTitleKeys.Select(k => GetString(data, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        if (!string.IsNullOrEmpty(topTitle)) return (topArtist, topTitle);

        foreach (var key in FlatStringKeys)
        {
            var value = GetString(data, key);
            if (!string.IsNullOrEmpty(value))
                return IcecastMetadataReader.ParseMetadataString(value);
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private async Task<(string Artist, string Title)?> ProbeId3Async(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_entries", "format_tags=title,artist", "-i", _playlistUrl })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        await stderrTask;

        if (process.ExitCode != 0) return null;

        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("tags", out var tags))
            return null;

        var title = GetString(tags, "title") ?? "";
        var artist = GetString(tags, "artist") ?? "";
        return string.IsNullOrEmpty(title) ? null : (artist, title);
    }
}
