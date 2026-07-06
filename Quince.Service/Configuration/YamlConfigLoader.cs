using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Quince.Service.Configuration;

public class YamlConfigLoader
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly ILogger<YamlConfigLoader>? _logger;

    public YamlConfigLoader(ILogger<YamlConfigLoader>? logger = null)
    {
        _logger = logger;
    }

    public IEnumerable<ChannelConfig> LoadAll(string configDir)
    {
        if (!Directory.Exists(configDir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(configDir, "*.yaml"))
        {
            ChannelConfig? config = null;
            try
            {
                var text = File.ReadAllText(file);
                config = _deserializer.Deserialize<ChannelConfig>(text);
                config.Filename = Path.GetFileName(file);
            }
            catch (Exception ex)
            {
                LogLoadError(file, ex);
            }
            if (config != null && !string.IsNullOrWhiteSpace(config.Name))
                yield return config;
        }
    }

    private void LogLoadError(string file, Exception ex)
    {
        if (_logger != null)
            _logger.LogError("Ошибка загрузки конфига {File}: {Error}", Path.GetFileName(file), ex.Message);
        else
            Console.Error.WriteLine($"[Config] Failed to load {file}: {ex.Message}");
    }

    public void Save(string configDir, ChannelConfig config)
    {
        var path = Path.Combine(configDir, config.Filename);
        var text = _serializer.Serialize(config);
        File.WriteAllText(path, text);
    }

    /// <summary>YAML round-trip so nested objects (Source/OutputFormat/...) aren't shared by reference with the original.</summary>
    public ChannelConfig Clone(ChannelConfig source)
    {
        var text = _serializer.Serialize(source);
        var clone = _deserializer.Deserialize<ChannelConfig>(text);
        clone.Filename = source.Filename;
        return clone;
    }

    public string Serialize(ChannelConfig config) => _serializer.Serialize(config);

    public void SaveApp(string configDir, AppConfig config)
    {
        var path = Path.Combine(configDir, "app.yaml");
        var text = _serializer.Serialize(config);
        File.WriteAllText(path, text);
    }

    public AppConfig LoadApp(string configDir)
    {
        var path = Path.Combine(configDir, "app.yaml");
        if (!File.Exists(path))
            return new AppConfig();

        try
        {
            var text = File.ReadAllText(path);
            return _deserializer.Deserialize<AppConfig>(text) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            LogLoadError(path, ex);
            return new AppConfig();
        }
    }
}
