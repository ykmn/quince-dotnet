using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class HlsMetadataReaderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ExtractFromJson_FmgidFormat()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"fmgid": {"artist": "George Michael", "name": "Freedom"}}"""));
        Assert.Equal(("George Michael", "Freedom"), result);
    }

    [Fact]
    public void ExtractFromJson_FmgidUsesSongWhenNameMissing()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"fmgid": {"artist": "Artist", "song": "MySong"}}"""));
        Assert.Equal(("Artist", "MySong"), result);
    }

    [Fact]
    public void ExtractFromJson_FlatArtistTitleFormat()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"artist": "Daft Punk", "title": "Get Lucky"}"""));
        Assert.Equal(("Daft Punk", "Get Lucky"), result);
    }

    [Fact]
    public void ExtractFromJson_FlatSongField()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"song": "Some Song"}"""));
        Assert.Equal(("", "Some Song"), result);
    }

    [Fact]
    public void ExtractFromJson_NowPlayingStringViaParse()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"now_playing": "Artist X - Track Y"}"""));
        Assert.Equal(("Artist X", "Track Y"), result);
    }

    [Fact]
    public void ExtractFromJson_CurrentStringField()
    {
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"current": "Solo Title"}"""));
        Assert.Equal(("", "Solo Title"), result);
    }

    [Fact]
    public void ExtractFromJson_EmptyObject_ReturnsNull()
    {
        Assert.Null(HlsMetadataReader.ExtractFromJson(Parse("{}")));
    }

    [Fact]
    public void ExtractFromJson_UnknownKeys_ReturnsNull()
    {
        Assert.Null(HlsMetadataReader.ExtractFromJson(Parse("""{"duration": 200, "bitrate": 128}""")));
    }

    [Fact]
    public void ExtractFromJson_TopLevelWinsOverFmgidPriorityOrder()
    {
        // fmgid checked first per legacy order: if fmgid has a usable title, it wins even
        // though a flat top-level field is also present.
        var result = HlsMetadataReader.ExtractFromJson(Parse("""{"fmgid": {"name": "From Fmgid"}, "title": "From Top Level"}"""));
        Assert.Equal(("", "From Fmgid"), result);
    }

    [Fact]
    public void BuildCandidateUrls_DerivesFromPlaylistDirectory()
    {
        var candidates = HlsMetadataReader.BuildCandidateUrls("https://host/11/playlist.m3u8").ToList();
        Assert.Equal(new[]
        {
            "https://host/11/metadata.json?format=fmgid&subformat=small",
            "https://host/11/metadata.json",
            "https://host/metadata.json",
        }, candidates);
    }

    [Fact]
    public async Task DiscoverMetadataUrlAsync_MalformedPlaylistUrl_ReturnsNullInsteadOfThrowing()
    {
        // BuildCandidateUrls parses the URL with `new Uri(...)`, which throws for a bare
        // hostname/relative string — DiscoverMetadataUrlAsync must not let that escape uncaught
        // (it's called from the "Определить наличие метаданных" button and mustn't ever leave
        // the UI stuck on an unhandled exception instead of a plain "not found" result).
        var result = await HlsMetadataReader.DiscoverMetadataUrlAsync("not a url", false, TimeSpan.FromSeconds(1));
        Assert.Null(result);
    }

    /// <summary>Regression test for the bug where, once the known URL check, JSON discovery, and
    /// the one-shot ID3 fallback had all failed at startup, the reader's background task simply
    /// completed forever — the only way to get metadata flowing again was a full channel config
    /// reload (which recreates the reader from scratch). This starts a reader against an endpoint
    /// that fails at startup, then flips it to succeed, and asserts the reader notices on its own
    /// via the background re-resolution loop, without ever being restarted.</summary>
    [Fact]
    public async Task RunAsync_SelfHealsInBackground_AfterInitialResolutionFails()
    {
        var origAttempts = HlsMetadataReader.DiscoveryAttempts;
        var origRetryDelay = HlsMetadataReader.DiscoveryRetryDelay;
        var origBackgroundInterval = HlsMetadataReader.BackgroundResolveInterval;
        HlsMetadataReader.DiscoveryAttempts = 1;
        HlsMetadataReader.DiscoveryRetryDelay = TimeSpan.FromMilliseconds(1);
        HlsMetadataReader.BackgroundResolveInterval = TimeSpan.FromMilliseconds(200);

        var found = false;
        // A raw TcpListener rather than System.Net.HttpListener: the latter is backed by
        // http.sys on Windows and needs either admin rights or a netsh urlacl reservation for
        // its prefix, which a unit test can't assume. Any well-formed HTTP/1.1 response over a
        // plain socket is enough for HttpClient.
        using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var prefix = $"http://127.0.0.1:{port}/";
        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await tcp.AcceptTcpClientAsync(serverCts.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(async () =>
                {
                    using var c = client;
                    using var stream = c.GetStream();
                    var buffer = new byte[1024];
                    // Just enough to drain the request line/headers; we don't care about the path.
                    try { await stream.ReadAsync(buffer, serverCts.Token); }
                    catch { return; }

                    var body = Volatile.Read(ref found)
                        ? """{"artist": "Recovered Artist", "title": "Recovered Title"}"""
                        : "{}";
                    var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                    var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
                    try
                    {
                        await stream.WriteAsync(headerBytes, serverCts.Token);
                        await stream.WriteAsync(bodyBytes, serverCts.Token);
                    }
                    catch { }
                }, serverCts.Token);
            }
        });

        try
        {
            MetadataEvent? received = null;
            var reader = new HlsMetadataReader(
                playlistUrl: prefix + "playlist.m3u8",
                allowInvalidSsl: false,
                onMetadata: evt => received = evt,
                channelName: "test-channel",
                ffprobePath: Path.Combine(Path.GetTempPath(), "no-such-ffprobe.exe"),
                log: NullLogger.Instance,
                knownMetadataUrl: prefix + "metadata.json");

            reader.Start();
            try
            {
                // At this point resolution should fail (server returns "{}", no recognizable
                // title) and the reader must fall into the background retry loop instead of its
                // task completing.
                await Task.Delay(300);
                Assert.True(reader.IsRunning, "reader task must not complete when all resolution attempts fail");
                Assert.False(reader.HasMetadata);

                Volatile.Write(ref found, true);

                // HasMetadata flips true as soon as the endpoint resolves, slightly before the
                // JsonPollLoopAsync's first fetch delivers the actual callback — wait for the
                // callback itself, not just the flag, to avoid a race.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (received == null && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(50);

                Assert.True(reader.HasMetadata, "reader should self-heal once the endpoint starts responding");
                Assert.NotNull(received);
                Assert.Equal("Recovered Artist", received!.Artist);
                Assert.Equal("Recovered Title", received!.Title);
            }
            finally
            {
                reader.Stop();
            }
        }
        finally
        {
            serverCts.Cancel();
            tcp.Stop();
            try { await serverTask; } catch { }
            HlsMetadataReader.DiscoveryAttempts = origAttempts;
            HlsMetadataReader.DiscoveryRetryDelay = origRetryDelay;
            HlsMetadataReader.BackgroundResolveInterval = origBackgroundInterval;
        }
    }

    /// <summary>Regression test for the bug that caused real metadata gaps in production (Rock
    /// FM/Relax FM, 2026-07-15/16): <c>JsonPollLoopAsync</c> caught ANY <see
    /// cref="OperationCanceledException"/> — including a <c>TaskCanceledException</c> thrown by
    /// the polling <see cref="HttpClient"/>'s own request timeout, which fires independently of
    /// the reader's cancellation token — as if it were a deliberate <see cref="Stop"/> and broke
    /// out of the loop for good. No error was ever logged (it hit the "cancelled, exit quietly"
    /// branch), so the reader's task just silently completed and metadata stopped forever until
    /// the whole service was restarted. This makes a request hang past <see
    /// cref="HlsMetadataReader.JsonPollTimeout"/> mid-poll and asserts the reader keeps polling
    /// and delivers a later event instead of dying.</summary>
    [Fact]
    public async Task JsonPollLoopAsync_SurvivesHttpTimeout_WithoutDying()
    {
        var origPollInterval = HlsMetadataReader.PollInterval;
        var origJsonPollTimeout = HlsMetadataReader.JsonPollTimeout;
        HlsMetadataReader.PollInterval = TimeSpan.FromMilliseconds(50);
        HlsMetadataReader.JsonPollTimeout = TimeSpan.FromMilliseconds(200);

        var requestCount = 0;
        using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var prefix = $"http://127.0.0.1:{port}/";
        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await tcp.AcceptTcpClientAsync(serverCts.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                var n = Interlocked.Increment(ref requestCount);
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    using var stream = c.GetStream();
                    var buffer = new byte[1024];
                    try { await stream.ReadAsync(buffer, serverCts.Token); } catch { return; }

                    if (n == 3)
                    {
                        // Simulate a stalled/slow response: accept the connection, read the
                        // request, then never write anything back — must trip the client-side
                        // timeout, not a graceful HTTP-level error.
                        try { await Task.Delay(Timeout.Infinite, serverCts.Token); } catch { }
                        return;
                    }

                    var title = n < 3 ? "Track1" : "Track2";
                    var body = $$"""{"artist": "Artist", "title": "{{title}}"}""";
                    var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                    var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
                    try
                    {
                        await stream.WriteAsync(headerBytes, serverCts.Token);
                        await stream.WriteAsync(bodyBytes, serverCts.Token);
                    }
                    catch { }
                }, serverCts.Token);
            }
        });

        try
        {
            var events = new List<MetadataEvent>();
            var reader = new HlsMetadataReader(
                playlistUrl: prefix + "playlist.m3u8",
                allowInvalidSsl: false,
                onMetadata: evt => events.Add(evt),
                channelName: "test-channel",
                ffprobePath: Path.Combine(Path.GetTempPath(), "no-such-ffprobe.exe"),
                log: NullLogger.Instance,
                knownMetadataUrl: prefix + "metadata.json");

            reader.Start();
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (events.Count == 0 && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(50);
                Assert.True(events.Count > 0, "first poll should have delivered metadata");
                Assert.True(reader.IsRunning);

                // Give the stalled request time to trip JsonPollTimeout and for polling to
                // resume — this is exactly the window in which the unfixed code would have
                // silently broken out of the loop.
                deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (!events.Exists(e => e.Title == "Track2") && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(50);

                Assert.True(reader.IsRunning, "reader task must not die from an HttpClient timeout");
                Assert.Contains(events, e => e.Title == "Track2");
            }
            finally
            {
                reader.Stop();
            }
        }
        finally
        {
            serverCts.Cancel();
            tcp.Stop();
            try { await serverTask; } catch { }
            HlsMetadataReader.PollInterval = origPollInterval;
            HlsMetadataReader.JsonPollTimeout = origJsonPollTimeout;
        }
    }
}
