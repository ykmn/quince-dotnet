using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>Holds the live app.yaml settings so the Settings dialog can edit them without an app restart.</summary>
public class AppSettingsService
{
    private readonly YamlConfigLoader _loader;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly string _configDir;

    public event Action? Changed;

    public AppConfig Current { get; private set; }

    public AppSettingsService(YamlConfigLoader loader, FileLoggerProvider fileLoggerProvider, IConfiguration configuration)
    {
        _loader = loader;
        _fileLoggerProvider = fileLoggerProvider;
        _configDir = PathResolver.Resolve(configuration["ConfigDir"], "config");
        Current = _loader.LoadApp(_configDir);
    }

    public void Save(AppConfig updated)
    {
        _loader.SaveApp(_configDir, updated);
        Current = updated;
        _fileLoggerProvider.UpdateSettings(updated);
        Changed?.Invoke();
    }
}
