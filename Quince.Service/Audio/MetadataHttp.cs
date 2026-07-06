namespace Quince.Service.Audio;

/// <summary>Small shared helpers for the metadata readers/probes — HTTP client construction
/// (with optional SSL-validation bypass, matching the legacy port's <c>_make_ssl_context</c>)
/// and reading the <c>icy-metaint</c> response header.</summary>
internal static class MetadataHttp
{
    public static HttpClient CreateClient(bool allowInvalidSsl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
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
