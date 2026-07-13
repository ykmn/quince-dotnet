namespace Quince.Service.Configuration;

public class AppConfig
{
    public string LogLevel { get; set; } = "INFO";
    public int LogRetentionDays { get; set; } = 30;
    public MeterColorsConfig MeterColors { get; set; } = new();

    /// <summary>Pause between reconnect attempts for a stream/soundcard channel whose capture
    /// backend just dropped — applies to every channel live (read at retry time, not cached at
    /// channel start), same as <see cref="AdKeywords"/>.</summary>
    public int ReconnectDelaySeconds { get; set; } = 3;

    /// <summary>How many consecutive reconnect attempts a channel gets before it's stopped
    /// outright and an ERROR is logged, instead of retrying forever. 0 = unlimited (never
    /// auto-stops on its own).</summary>
    public int ReconnectMaxAttempts { get; set; } = 0;

    /// <summary>Case-insensitive substring matches against a metadata event's title/artist that mark
    /// it as class "C" (advertisement) in the metadata CSV instead of "M" (music) — see
    /// <see cref="Audio.MetadataWriter"/>.</summary>
    public List<string> AdKeywords { get; set; } = new() { "Реклама", "Reklama", "Commercial" };

    /// <summary>Same idea as <see cref="AdKeywords"/> but marks class "N" (news) instead of "C".</summary>
    public List<string> NewsKeywords { get; set; } = new() { "Новости", "News", "Novosti" };

    /// <summary>UI display language — "ru" or "en". Does not affect log messages, metadata, or
    /// channel config field names, only the interactive UI text served by <see cref="Services.LocalizationService"/>.</summary>
    public string UiLanguage { get; set; } = "ru";

    /// <summary>Whether channel cards show their True Peak meter (<c>ChannelCard</c>'s level-widget).
    /// Off by default — when off, the card doesn't just hide the meter, it also stops reacting to
    /// level updates for it, so the per-card indicator module does no work at all. The docked
    /// per-channel indicators panel (▦ button) is unaffected either way.</summary>
    public bool ShowTpIndicators { get; set; } = false;

    /// <summary>How many seconds of audio <see cref="Audio.PlayoutBuffer"/> banks before releasing
    /// anything to the level meter/browser listen-in (docs/HISTORY.md #61), for continuous sources
    /// (Icecast/soundcard/Livewire) — HLS channels size themselves automatically from their own
    /// measured playlist segment duration instead (docs/HISTORY.md #126,
    /// <see cref="Audio.HlsSegmentDurationService"/>), falling back to this same value only until a
    /// measurement succeeds. The fixed added latency traded for hiding producer-side gaps up to this
    /// depth. Read fresh at each channel start (see <see cref="Audio.ChannelEngine"/>), not applied
    /// to already-running channels until they restart. Default lowered from the old flat 12s
    /// (docs/HISTORY.md #61) to 2s in #126 now that non-HLS sources no longer need to be sized for
    /// HLS's worst case — existing installs keep whatever value is already in their <c>app.yaml</c>
    /// until re-saved in Настройки → Индикаторы.</summary>
    public double PlayoutBufferSeconds { get; set; } = 2.0;

    /// <summary>How long a login session stays valid (both the cookie's max-age and the server-side
    /// session record) — only meaningful when config/ldap.yaml enables authentication. Default: 1 week,
    /// same as apricot2.</summary>
    public int AuthSessionTtlSeconds { get; set; } = 7 * 24 * 3600;

    /// <summary>Network adapter used for every Livewire channel's multicast join (both audio RTP and
    /// <see cref="Audio.Livewire.LivewireDiscoveryService"/>'s Advertisement listener) — one setting
    /// for the whole app, not per-channel, since every Livewire channel is by definition on the same
    /// physical AoIP network. Empty string ("нет") means Livewire is not used on this machine: no
    /// discovery, and any configured <c>livewire</c> channel fails to start with a clear error instead
    /// of silently picking a NIC. Read once at startup for discovery and once per channel start for
    /// capture — changing it live requires a restart, same as <see cref="Audio.Livewire.LivewireDiscoveryService.AdvertisementPort"/>.</summary>
    public string LivewireNic { get; set; } = "";

    /// <summary>Marks this instance as a debug/test build in the UI (topbar brand, browser tab
    /// title, login page, About dialog all get an " — отладочная версия" suffix) — lets whoever's
    /// running it at a glance tell it apart from a normal production instance, e.g. when a debug
    /// build is running side-by-side with (or instead of) a production one during field testing.</summary>
    public bool Develop { get; set; } = false;

    /// <summary>The address(es) Kestrel listens on, same format as ASP.NET Core's own <c>Urls</c>
    /// setting/<c>ASPNETCORE_URLS</c> (semicolon-separated for more than one). Default listens on
    /// every network interface on port 5000 — <c>http://localhost:5000</c> would restrict access to
    /// the same machine only (see docs/HISTORY.md's Urls entry for that exact confusion). Read once
    /// at startup, before the rest of the app's <c>settings.yaml</c>-driven configuration even exists
    /// (<see cref="Program"/> calls <c>UseUrls</c> right after loading this file, before
    /// <c>WebApplicationBuilder.Build()</c>) — changing it requires an app restart, same as
    /// <see cref="LivewireNic"/>. Previously lived in the now-removed <c>appsettings.json</c>
    /// (docs/HISTORY.md #128) — moved here so the app has a single config file.</summary>
    public string Urls { get; set; } = "http://0.0.0.0:5000";
}

public class MeterColorsConfig
{
    public double ZoneYellowDb { get; set; } = -18.0;
    public double ZoneRedDb { get; set; } = -6.0;
    public string ColorGreen { get; set; } = "#28a428";
    public string ColorYellow { get; set; } = "#ccaa00";
    public string ColorRed { get; set; } = "#cc1818";
}
