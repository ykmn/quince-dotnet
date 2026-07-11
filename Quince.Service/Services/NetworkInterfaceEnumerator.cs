using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Quince.Service.Services;

/// <summary>
/// Lists local network adapters suitable for joining a Livewire AoIP multicast group — the NIC
/// picker's data source in <c>AppSettingsDialog</c> (one app-wide choice, see
/// <see cref="Configuration.AppConfig.LivewireNic"/>), and the source of the per-adapter IPv4
/// address <see cref="Audio.LivewireCapture"/> passes to ffmpeg's <c>-localaddr</c> and
/// <see cref="Audio.Livewire.LivewireDiscoveryService"/> passes to
/// <see cref="System.Net.Sockets.Socket"/>'s multicast join. Mirrors
/// <see cref="Audio.SoundcardCapture.EnumerateDevices"/>'s shape (a plain record list, no live
/// device object kept around) so the UI pattern is identical for both device pickers.
/// </summary>
public static class NetworkInterfaceEnumerator
{
    public readonly record struct NicInfo(string Id, string Name, string Description, string IPv4Address, bool IsUp);

    /// <summary>Only adapters that are currently up and have at least one IPv4 unicast address —
    /// joining a multicast group needs a real local IPv4 address to bind to, so anything without one
    /// (a disabled adapter, IPv6-only, etc.) wouldn't be a usable choice anyway.</summary>
    public static List<NicInfo> EnumerateNics()
    {
        var result = new List<NicInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipv4 = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 == null) continue;

            result.Add(new NicInfo(nic.Id, nic.Name, nic.Description, ipv4.Address.ToString(),
                nic.OperationalStatus == OperationalStatus.Up));
        }
        return result;
    }

    /// <summary>Resolves a stored <see cref="Configuration.AppConfig.LivewireNic"/> (an adapter
    /// <see cref="NetworkInterface.Id"/>) to its current IPv4 address — null if that adapter is no
    /// longer present/enumerable (unplugged, renamed, disabled), which callers should treat as a
    /// startup failure the same way <see cref="Audio.SoundcardCapture.ResolveDeviceIndex"/> callers
    /// do for a vanished sound device.</summary>
    public static string? ResolveNicIPv4(string nicId)
    {
        if (string.IsNullOrEmpty(nicId)) return null;
        return EnumerateNics().FirstOrDefault(n => n.Id == nicId).IPv4Address;
    }
}
