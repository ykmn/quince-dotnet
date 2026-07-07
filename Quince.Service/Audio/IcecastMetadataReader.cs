using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>
/// Reads ICY inline metadata from an Icecast stream (the <c>icy-metaint</c>/<c>StreamTitle</c>
/// protocol) and fires <see cref="MetadataEvent"/>s on change. Direct port of the legacy Python
/// implementation's <c>IcecastMetadataReader</c> (<c>src/audio/metadata_icecast.py</c>) — same
/// reconnect backoff sequence, same "no icy-metaint → give up, don't retry" behaviour, same
/// "Artist - Title" splitting.
/// </summary>
public sealed class IcecastMetadataReader : IMetadataReader
{
    private static readonly int[] BackoffSequence = { 1, 2, 4, 8, 16, 30 };
    private static readonly Regex StreamTitleRegex = new(@"StreamTitle='([^']*)'", RegexOptions.Compiled);
    private static readonly Regex ArtistTitleRegex = new(@"^(.*?)\s+-\s+(.*)", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly string _url;
    private readonly bool _allowInvalidSsl;
    private readonly Action<MetadataEvent>? _onMetadata;
    private readonly string _channelName;
    private readonly ILogger _log;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private string _lastRaw = "";
    private volatile bool _hasMetadata;

    public IcecastMetadataReader(string url, bool allowInvalidSsl, Action<MetadataEvent>? onMetadata, string channelName, ILogger log)
    {
        _url = url;
        _allowInvalidSsl = allowInvalidSsl;
        _onMetadata = onMetadata;
        _channelName = channelName;
        _log = log;
    }

    /// <summary>True once the server has confirmed it sends ICY metadata (icy-metaint &gt; 0).</summary>
    public bool HasMetadata => _hasMetadata;
    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(15)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
    }

    /// <summary>Split "Artist - Title" on the first " - " separator. No match → ("", raw.Trim()).</summary>
    internal static (string Artist, string Title) ParseMetadataString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        var match = ArtistTitleRegex.Match(raw);
        return match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) : ("", raw.Trim());
    }

    /// <summary>Quick check: does this Icecast URL support ICY metadata? Only inspects response
    /// headers — never reads the audio body. Returns false on any error.</summary>
    public static async Task<bool> ProbeAsync(string url, bool allowInvalidSsl, TimeSpan timeout)
    {
        try
        {
            using var client = MetadataHttp.CreateClient(allowInvalidSsl, timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            request.Headers.UserAgent.ParseAdd(UserAgents.RandomDesktop());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return MetadataHttp.TryGetIcyMetaInt(response, out _);
        }
        catch
        {
            return false;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var scope = _log.BeginScope(new Dictionary<string, object> { ["Channel"] = _channelName });
        var backoffIdx = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = MetadataHttp.CreateClient(_allowInvalidSsl, TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                request.Headers.UserAgent.ParseAdd(UserAgents.RandomDesktop());
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!MetadataHttp.TryGetIcyMetaInt(response, out var metaInt))
                {
                    _log.LogInformation("[{Channel}] Stream has no icy-metaint, metadata unavailable: {Url}", _channelName, _url);
                    return; // matches legacy: an explicit "no metadata" response is not retried
                }

                _hasMetadata = true;
                backoffIdx = 0;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await ReadLoopAsync(stream, metaInt, ct);
                // A clean EOF (stream ended) falls through here and reconnects immediately,
                // same as the legacy reader's outer while loop.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                var delay = BackoffSequence[Math.Min(backoffIdx, BackoffSequence.Length - 1)];
                _log.LogWarning("[{Channel}] IcecastMetadataReader error ({Message}), reconnecting in {Delay}s", _channelName, ex.Message, delay);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { break; }
                backoffIdx++;
            }
        }
    }

    private async Task ReadLoopAsync(Stream stream, int metaInt, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await SkipExactAsync(stream, metaInt, ct)) return;

            var lenByte = stream.ReadByte();
            if (lenByte < 0) return;
            if (lenByte == 0) continue;

            var metaBytes = new byte[lenByte * 16];
            if (!await ReadExactAsync(stream, metaBytes, ct)) return;
            var metaText = Encoding.UTF8.GetString(metaBytes).TrimEnd('\0');

            var match = StreamTitleRegex.Match(metaText);
            if (!match.Success) continue;

            var raw = match.Groups[1].Value.Trim();
            if (raw == _lastRaw) continue;
            _lastRaw = raw;

            var (artist, title) = ParseMetadataString(raw);
            try { _onMetadata?.Invoke(new MetadataEvent(raw, artist, title, DateTimeOffset.Now)); }
            catch (Exception ex) { _log.LogError(ex, "[{Channel}] on_metadata callback raised", _channelName); }
        }
    }

    private static async Task<bool> SkipExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(Math.Max(count, 1), 65536)];
        var remaining = count;
        while (remaining > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (n == 0) return false; // EOF
            remaining -= n;
        }
        return true;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) return false; // EOF
            offset += n;
        }
        return true;
    }
}
