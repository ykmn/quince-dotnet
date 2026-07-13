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

    /// <summary>Local time, formatted <c>yyyy-MM-dd HH:mm:ss</c> (same convention as the app's own log
    /// lines and metadata CSV — see <c>FileLoggerProvider</c>/<c>MetadataWriter</c>) — when this entry
    /// was last confirmed by Advertisement/LWRP traffic, not when the cache file itself was written.
    /// Human-readable since docs/HISTORY.md #130 (previously Unix seconds); <see cref="Audio.Livewire.LivewireDiscoveryService"/>'s
    /// loader still accepts the old numeric format too, so upgrading doesn't lose timestamps already on
    /// disk.</summary>
    public string LastSeen { get; set; } = "";
}
