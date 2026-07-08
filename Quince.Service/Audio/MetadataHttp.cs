namespace Quince.Service.Audio;

/// <summary>Small shared helpers for the metadata readers/probes — HTTP client construction
/// (with optional SSL-validation bypass, matching the legacy port's <c>_make_ssl_context</c>)
/// and reading the <c>icy-metaint</c> response header.</summary>
internal static class MetadataHttp
{
    public static HttpClient CreateClient(bool allowInvalidSsl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            // These are direct calls to known streaming/metadata endpoints — no proxy needed. Left
            // at the default (true), a fresh HttpClientHandler can trigger WPAD/system-proxy
            // auto-detection (a synchronous-ish network lookup) on first use, adding up to several
            // seconds of latency per handler instance — worth ruling out unconditionally, not just
            // for the reused-client fix below.
            UseProxy = false,
        };
        if (allowInvalidSsl)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static bool TryGetIcyMetaInt(HttpResponseMessage response, out int metaInt)
    {
        metaInt = 0;
        if (response.Headers.TryGetValues("icy-metaint", out var values) ||
            response.Content.Headers.TryGetValues("icy-metaint", out values))
        {
            return int.TryParse(values.FirstOrDefault(), out metaInt) && metaInt > 0;
        }
        return false;
    }
}
