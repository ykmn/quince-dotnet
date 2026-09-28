using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Quince.Service.Configuration;
using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class ZabbixMonitoringTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public void ListeningIsNotRecording_AndStoppedAudioIsUnknown()
    {
        var config = new ChannelConfig { Name = "Радио", Filename = "radio.yaml" };
        var live = ChannelTelemetry.Create(config, new EngineStatus(IsRecording: true), null,
            new LevelReading(TruePeakDb: -12), Now, null, Now);
        Assert.Equal(0, live.Recording);
        Assert.Equal(2, live.Audio);
        var stopped = ChannelTelemetry.Create(config, new EngineStatus(), null,
            new LevelReading(TruePeakDb: -12), Now, null, Now);
        Assert.Equal(0, stopped.Audio);
        Assert.Equal(0, stopped.Metadata);
    }

    [Fact]
    public void MetadataExpires_AndStaleAudioIsNotReportedAsSound()
    {
        var config = new ChannelConfig { Source = new SourceConfig { MetadataUrl = "icy" } };
        var status = new EngineStatus(IsRecording: true, IsFileRecording: true, MetadataOk: true);
        var stale = ChannelTelemetry.Create(config, status, Now.AddSeconds(-301),
            new LevelReading(TruePeakDb: -10), Now.AddSeconds(-16), null, Now);
        Assert.Equal(1, stale.Metadata);
        Assert.Equal(0, stale.Audio);
        var current = ChannelTelemetry.Create(config, status, Now.AddSeconds(-2), null, null, null, Now);
        Assert.Equal(2, current.Metadata);
    }

    [Fact]
    public void SilencePauseIsNotFileRecording()
    {
        var config = new ChannelConfig { SilenceDetector = new() { Enabled = true } };
        var status = new EngineStatus(IsRecording: true, IsFileRecording: true, IsSilent: true);
        var result = ChannelTelemetry.Create(config, status, null,
            new LevelReading(), Now, null, Now);
        Assert.Equal(0, result.Recording);
        Assert.Equal(1, result.Audio);
        config.SilenceDetector.DontStopRecording = true;
        Assert.Equal(1, ChannelTelemetry.Create(config, status, null, null, null, null, Now).Recording);
    }

    [Theory]
    [InlineData(800_582_400, 92.66)]
    [InlineData(1_728_000_000, 100)]
    [InlineData(0, 0)]
    public void ForecastMatchesCard(long used, double expected)
    {
        var config = new ChannelConfig { RetentionDays = 1,
            OutputFormat = new() { Mode = "custom", FileFormat = "mp3", BitrateKbps = 80 } };
        var result = ChannelTelemetry.Create(config, null, null, null, null,
            new FolderUsage(used, Now), Now);
        Assert.Equal(expected, result.ForecastPct!.Value, 2);
        config.RetentionDays = 0;
        Assert.Null(ChannelTelemetry.Create(config, null, null, null, null,
            new FolderUsage(used, Now), Now).ForecastPct);
        Assert.Null(ChannelTelemetry.Create(config, null, null, null, null, null, Now).ForecastPct);
    }

    [Fact]
    public void StaleDiskScanCannotLookLikeCurrentMeasurement()
    {
        var result = ChannelTelemetry.Create(new ChannelConfig(), null, null, null, null,
            new FolderUsage(100, Now.AddMinutes(-11)), Now);
        Assert.Null(result.ForecastPct);
        Assert.Equal(0, result.DiskAvailable);
    }

    [Theory]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("::ffff:127.0.0.1", false, true)]
    [InlineData("192.0.2.1", false, false)]
    [InlineData("127.0.0.1", true, false)]
    public void EndpointOnlyAllowsDirectLoopback(string address, bool forwarded, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (forwarded) context.Request.Headers["X-Forwarded-For"] = "192.0.2.1";
        Assert.Equal(expected, ZabbixEndpoint.IsLocalRequest(context));
    }

    [Fact]
    public async Task ChannelIdentitySurvivesRenameAndReload_ButCloneGetsNewIdentity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quince-zabbix-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "stations"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "stations", "legacy.yaml"), "name: Старое имя\n");
            var loader = new YamlConfigLoader();
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConfigDir"] = dir }).Build();
            var manager = new ChannelManager(loader, NullLogger<ChannelManager>.Instance, settings);
            await manager.StartAsync(default);
            var original = Assert.Single(manager.Channels);
            var id = original.MonitoringId;
            Assert.Matches("^[a-f0-9]{32}$", id);
            Assert.Equal(id, Assert.Single(loader.LoadAll(Path.Combine(dir, "stations"))).MonitoringId);
            var edited = loader.Clone(original);
            edited.Name = "Новое имя";
            manager.Update(original.Filename, edited);
            Assert.Equal(id, Assert.Single(loader.LoadAll(Path.Combine(dir, "stations"))).MonitoringId);
            Assert.NotEqual(id, manager.Clone(edited.Filename).MonitoringId);
        }
        finally { Directory.Delete(dir, true); }
    }
}
