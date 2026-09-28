using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class IcecastMonitoringTests
{
    [Fact]
    public async Task RepeatedTitleAndUnchangedBlockRefreshReceipt_WithoutDuplicateEvents()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var blocks = Channel.CreateUnbounded<byte[]>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096], cancellation.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nicy-metaint: 1\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n\r\n"), cancellation.Token);
            await foreach (var block in blocks.Reader.ReadAllAsync(cancellation.Token))
                await stream.WriteAsync(block, cancellation.Token);
        });
        var events = 0;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reader = new IcecastMetadataReader($"http://127.0.0.1:{port}/", false,
            _ => Interlocked.Increment(ref events), "monitor-test", NullLogger.Instance);
        try
        {
            reader.Start();
            var title = new byte[34]; // one audio byte, length=2, 32 bytes of metadata
            title[1] = 2;
            Encoding.UTF8.GetBytes("StreamTitle='A - B';").CopyTo(title, 2);
            await blocks.Writer.WriteAsync(title);
            await WaitForReceiptAfter(reader, null);
            var first = reader.LastReceivedAt;
            await Task.Delay(20);
            await blocks.Writer.WriteAsync(title);
            await WaitForReceiptAfter(reader, first);
            var second = reader.LastReceivedAt;
            await Task.Delay(20);
            await blocks.Writer.WriteAsync(new byte[2]); // zero-length ICY metadata block
            await WaitForReceiptAfter(reader, second);
            Assert.Equal(1, Volatile.Read(ref events));
        }
        finally
        {
            cancellation.Cancel();
            blocks.Writer.TryComplete();
            try { await server; } catch (OperationCanceledException) { }
            reader.Stop();
        }
    }

    private static async Task WaitForReceiptAfter(IcecastMetadataReader reader, DateTimeOffset? previous)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while ((reader.LastReceivedAt == null || reader.LastReceivedAt <= previous) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotNull(reader.LastReceivedAt);
        if (previous != null) Assert.True(reader.LastReceivedAt > previous);
    }
}
