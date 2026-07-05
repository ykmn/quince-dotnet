namespace Quince.Service.Configuration;

public class AppConfig
{
    public string LogLevel { get; set; } = "INFO";
    public int LogRetentionDays { get; set; } = 30;
    public MeterColorsConfig MeterColors { get; set; } = new();
}

public class MeterColorsConfig
{
    public double ZoneYellowDb { get; set; } = -18.0;
    public double ZoneRedDb { get; set; } = -6.0;
    public string ColorGreen { get; set; } = "#28a428";
    public string ColorYellow { get; set; } = "#ccaa00";
    public string ColorRed { get; set; } = "#cc1818";
}
