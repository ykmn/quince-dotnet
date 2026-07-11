using System.Net;
using Quince.Service.Audio.Livewire;
using Xunit;

namespace Quince.Service.Tests.Audio;

/// <summary>
/// Covers <see cref="LivewireDiscoveryService.Merge"/> — the rule that a later Advertisement sighting
/// with an empty Name/DeviceName must not erase a value a previous, fuller sighting already
/// established. This was a real bug: some devices alternate between full bursts (with ATRN/PSNM) and
/// "lite" ones (without) for the *same* channel, and a plain dictionary overwrite made names flicker
/// in and out depending on which packet happened to be the most recent one received.
/// </summary>
public class LivewireDiscoveryMergeTests
{
    private static readonly IPAddress Addr = IPAddress.Parse("239.192.0.1");
    private static readonly IPAddress DeviceIp = IPAddress.Parse("172.22.0.49");
    private static readonly DateTimeOffset T0 = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(5);

    [Fact]
    public void Merge_IncomingEmptyName_KeepsExistingName()
    {
        var existing = new DiscoveredLivewireChannel(1, "Novoe Expres", "lwwd", DeviceIp, Addr, T0);
        var incoming = new DiscoveredLivewireChannel(1, "", "", DeviceIp, Addr, T1);

        var merged = LivewireDiscoveryService.Merge(existing, incoming);

        Assert.Equal("Novoe Expres", merged.Name);
        Assert.Equal("lwwd", merged.DeviceName);
        Assert.Equal(T1, merged.LastSeen); // still advances — this channel IS still being seen
    }

    [Fact]
    public void Merge_IncomingHasNewName_OverwritesOldName()
    {
        var existing = new DiscoveredLivewireChannel(1, "Old Name", "OldDevice", DeviceIp, Addr, T0);
        var incoming = new DiscoveredLivewireChannel(1, "New Name", "NewDevice", DeviceIp, Addr, T1);

        var merged = LivewireDiscoveryService.Merge(existing, incoming);

        Assert.Equal("New Name", merged.Name);
        Assert.Equal("NewDevice", merged.DeviceName);
    }

    [Fact]
    public void Merge_OnlyDeviceNameMissing_NameStillUpdatesIndependently()
    {
        var existing = new DiscoveredLivewireChannel(1, "Old Name", "lwwd", DeviceIp, Addr, T0);
        var incoming = new DiscoveredLivewireChannel(1, "New Name", "", DeviceIp, Addr, T1);

        var merged = LivewireDiscoveryService.Merge(existing, incoming);

        Assert.Equal("New Name", merged.Name); // non-empty incoming wins
        Assert.Equal("lwwd", merged.DeviceName); // empty incoming doesn't erase it
    }

    [Fact]
    public void Merge_IncomingMissingDeviceIp_KeepsExistingDeviceIp()
    {
        var existing = new DiscoveredLivewireChannel(1, "Name", "Device", DeviceIp, Addr, T0);
        var incoming = new DiscoveredLivewireChannel(1, "Name", "Device", null, Addr, T1);

        var merged = LivewireDiscoveryService.Merge(existing, incoming);

        Assert.Equal(DeviceIp, merged.DeviceIp); // "lite" bursts don't repeat INIP either — don't erase it
    }
}
