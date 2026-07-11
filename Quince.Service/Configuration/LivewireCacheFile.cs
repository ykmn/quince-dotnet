namespace Quince.Service.Configuration;

/// <summary>Maps to config/livewire.yaml — a persisted snapshot of Livewire channels discovered via
/// Advertisement/LWRP (see Audio/Livewire/LivewireDiscoveryService.cs), so the channel picker in
/// ChannelEditDialog has something to show immediately after a restart instead of starting empty and
/// waiting for fresh broadcast/query traffic to trickle back in. Written only when the user clicks
/// "Обновить" in the Livewire tab, not continuously — a cache is a convenience snapshot, not a source
/// of truth, so it doesn't need to track every discovery in real time.</summary>
public class LivewireCacheFile
{
    public List<LivewireCacheEntry> Channels { get; set; } = new();
}

public class LivewireCacheEntry
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceIp { get; set; } = "";

    /// <summary>Unix seconds — when this entry was last confirmed by Advertisement/LWRP traffic, not
    /// when the cache file itself was written.</summary>
    public long LastSeen { get; set; }
}
