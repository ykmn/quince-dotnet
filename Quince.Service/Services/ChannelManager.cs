using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class ChannelManager : IHostedService
{
    private readonly YamlConfigLoader _loader;
    private readonly ILogger<ChannelManager> _logger;
    private readonly string _configDir;
    private readonly List<ChannelConfig> _channels = new();

    public IReadOnlyList<ChannelConfig> Channels => _channels;

    public ChannelManager(YamlConfigLoader loader, ILogger<ChannelManager> logger, IConfiguration configuration)
    {
        _loader = loader;
        _logger = logger;
        _configDir = PathResolver.Resolve(configuration["ConfigDir"], "config");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Айва стартует, папка конфигов: {Dir}", _configDir);
        Directory.CreateDirectory(_configDir);

        _channels.Clear();
        foreach (var config in _loader.LoadAll(_configDir))
        {
            _channels.Add(config);
            using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = config.Name }))
            {
                _logger.LogInformation("Канал загружен из {File}", config.Filename);
            }
        }

        _logger.LogInformation("Загружено каналов: {Count}", _channels.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Айва останавливается");
        return Task.CompletedTask;
    }
}
