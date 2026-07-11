using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio.Livewire;

/// <summary>
/// Best-effort TCP query of a single Livewire node's LWRP server (port 93) for its "SRC" table — used
/// to backfill channel names that the Advertisement broadcast didn't carry (some devices only send a
/// number, no <c>PSNM</c> — see <see cref="LivewireAdvertisementParser"/>). Not every node runs LWRP
/// (some don't implement it at all) and connecting too often to one can reportedly upset real hardware
/// (per the open-source client's own README) — callers should query each node IP at most once per
/// discovery run, not repeatedly.
/// </summary>
public static class LwrpClient
{
    public const int Port = 93;

    public static async Task<IReadOnlyDictionary<int, string>> QuerySourceNamesAsync(string host, TimeSpan timeout, ILogger? logger = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, Port, cts.Token);

            using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("SRC\n"), cts.Token);

            var response = await ReadUntilEndAsync(stream, cts.Token);
            return LwrpParser.ParseSourceNames(response);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            logger?.LogDebug(ex, "LWRP: не удалось получить список источников с {Host}:{Port}", host, Port);
            return new Dictionary<int, string>();
        }
    }

    /// <summary>Accumulates response bytes until the "BEGIN...END" block closes or the token's timeout
    /// fires — whichever comes first. Returning whatever was read so far on timeout (rather than
    /// throwing it away) means a slow-but-working device still yields a partial, still-useful result.</summary>
    private static async Task<string> ReadUntilEndAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (sb.ToString().TrimEnd().EndsWith("END", StringComparison.Ordinal)) break;
            }
        }
        catch (OperationCanceledException) { /* timed out — return whatever arrived so far */ }
        return sb.ToString();
    }
}
