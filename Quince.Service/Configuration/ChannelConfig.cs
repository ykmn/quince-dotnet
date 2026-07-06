namespace Quince.Service.Configuration;

public class ChannelConfig
{
    public string Name { get; set; } = "";
    public SourceConfig Source { get; set; } = new();
    public InputFormatConfig InputFormat { get; set; } = new();
    public OutputFormatConfig OutputFormat { get; set; } = new();
    public string SavePath { get; set; } = "";
    public string DateFolderFormat { get; set; } = "YYYY-MM-DD";
    public string FileNameFormat { get; set; } = "hh-mm-ss";
    public int FileDurationMinutes { get; set; } = 60;
    public bool RecordAudio { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public bool AutoStart { get; set; } = false;
    public SilenceDetectorConfig SilenceDetector { get; set; } = new();
    public string MetadataPath { get; set; } = "";

    [YamlDotNet.Serialization.YamlIgnore]
    public string Filename { get; set; } = "";
}

public class SourceConfig
{
    public string Type { get; set; } = "stream";
    public string DeviceName { get; set; } = "";
    public int DeviceIndex { get; set; } = -1;
    public string DeviceUid { get; set; } = "";
    public string Url { get; set; } = "";
    public string StreamType { get; set; } = "icecast";
    public int HlsBitrateIndex { get; set; } = 0;
    public bool AllowHttp { get; set; } = false;
    public bool AllowInvalidSsl { get; set; } = false;
    public string MetadataUrl { get; set; } = "";
    public int ReconnectDelaySeconds { get; set; } = 3;
}

public class InputFormatConfig
{
    public int SampleRate { get; set; } = 0;
    public int BitDepth { get; set; } = 0;
    public int Channels { get; set; } = 0;
    public int Bitrate { get; set; } = 0;
    public string Codec { get; set; } = "";
}

public class OutputFormatConfig
{
    public string Mode { get; set; } = "original";
    public string FileFormat { get; set; } = "mp3";
    public int SampleRate { get; set; } = 44100;
    public int BitDepth { get; set; } = 16;
    public int Channels { get; set; } = 2;
    public int BitrateKbps { get; set; } = 96;
}

public class SilenceDetectorConfig
{
    public bool Enabled { get; set; } = false;
    public double ThresholdDbfs { get; set; } = -60.0;
    public double TriggerSeconds { get; set; } = 3.0;
    public double ResumeSeconds { get; set; } = 1.0;
}
