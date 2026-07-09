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

    /// <summary>How many seconds of audio <see cref="Audio.PlayoutBuffer"/> banks before releasing
    /// anything to the level meter/browser listen-in (docs/HISTORY.md #61) — the fixed added latency
    /// traded for hiding producer-side gaps (e.g. HLS's periodic wait for the next live segment) up
    /// to this depth. Read fresh at each channel start (see <see cref="Audio.ChannelEngine"/>), not
    /// applied to already-running channels until they restart.</summary>
    public double PlayoutBufferSeconds { get; set; } = Audio.PlayoutBuffer.DefaultTargetDelaySeconds;
}

public class MeterColorsConfig
{
    public double ZoneYellowDb { get; set; } = -18.0;
    public double ZoneRedDb { get; set; } = -6.0;
    public string ColorGreen { get; set; } = "#28a428";
    public string ColorYellow { get; set; } = "#ccaa00";
    public string ColorRed { get; set; } = "#cc1818";
}
