using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>
/// Auth support for radio-holding.ru streams fronted by radioplayer.ru (e.g. "Маруся FM",
/// cdn.radio-holding.ru/marusya_default). That CDN requires a fresh, short-lived <c>st</c>/<c>gts</c>
/// token pair in the query string PLUS a <c>Referer: https://radioplayer.ru/</c> header — without
/// either one it doesn't error, it silently 302-redirects to the station's own marketing site
/// (marus.fm) instead of serving audio, which is what made this look like a generic connection
/// failure. Found by reading radioplayer.ru's own bundle.js: right before playback it calls
/// <see cref="TokenEndpoint"/> for a fresh token and stamps it onto the stream URL — a token copied
/// once from the browser's network tab eventually breaks because <c>gts</c> is its own unix-time
/// expiry, so this fetches a fresh one on every (re)connect rather than storing one in config.
/// </summary>
public static class RadioplayerAuth
{
    private const string TokenEndpoint = "https://api.radioplayer.ru/api/web/site/gts";

    /// <summary>ffmpeg <c>-headers</c> value the CDN requires alongside the token — verified by hand:
    /// a valid token without this header still gets redirected to the marketing site.</summary>
    public const string RefererHeaderArg = "Referer: https://radioplayer.ru/\r\n";

    /// <summary>Fetches a fresh token from <see cref="TokenEndpoint"/> and stamps it onto <paramref name="url"/>.
    /// Never throws — on any failure (network, unexpected response shape) it logs a warning and
    /// returns <paramref name="url"/> unchanged, so the caller's normal reconnect/backoff loop is
    /// what ends up retrying rather than this needing its own error handling.</summary>
    public static async Task<string> ApplyTokenAsync(string url, ILogger log, CancellationToken ct)
    {
        try
        {
            using var client = MetadataHttp.CreateClient(allowInvalidSsl: false, TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(TokenEndpoint, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var st = doc.RootElement.GetProperty("st").GetString();
            var gts = doc.RootElement.GetProperty("gts").GetRawText();
            if (string.IsNullOrEmpty(st)) throw new InvalidOperationException("radioplayer.ru gts ответил без 'st'");

            return ApplyToken(url, st, gts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось получить токен radioplayer.ru — подключение пойдёт без него и, скорее всего, будет отклонено");
            return url;
        }
    }

    /// <summary>Pure URL-rewriting step, split out from <see cref="ApplyTokenAsync"/> so it's testable
    /// without a network call: sets/replaces the <c>st</c> and <c>gts</c> query parameters.</summary>
    internal static string ApplyToken(string url, string st, string gts)
    {
        var uriBuilder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(uriBuilder.Query);
        query["st"] = st;
        query["gts"] = gts;
        uriBuilder.Query = query.ToString();
        return uriBuilder.Uri.ToString();
    }
}
