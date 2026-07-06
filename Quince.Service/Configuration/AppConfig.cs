namespace Quince.Service.Configuration;

public class AppConfig
{
    public string LogLevel { get; set; } = "INFO";
    public int LogRetentionDays { get; set; } = 30;
    public MeterColorsConfig MeterColors { get; set; } = new();
    public string OutputDeviceUid { get; set; } = "";

    /// <summary>Case-insensitive substring matches against a metadata event's title/artist that mark
    /// it as class "C" (advertisement) in the metadata CSV instead of "M" (music) — see
    /// <see cref="Audio.MetadataWriter"/>.</summary>
    public List<string> AdKeywords { get; set; } = new() { "Реклама", "Reklama", "Commercial" };
}

public class MeterColorsConfig
{
    public double ZoneYellowDb { get; set; } = -18.0;
    public double ZoneRedDb { get; set; } = -6.0;
    public string ColorGreen { get; set; } = "#28a428";
    public string ColorYellow { get; set; } = "#ccaa00";
    public string ColorRed { get; set; } = "#cc1818";
}
