using Quince.Service.Configuration;

namespace Quince.Service.Services;

public class ChannelManager : IHostedService
{
    private readonly YamlConfigLoader _loader;
    private readonly ILogger<ChannelManager> _logger;
    private readonly string _configDir;
    private readonly object _lock = new();
    private readonly List<ChannelConfig> _channels = new();

    public string ConfigDir => _configDir;

    /// <summary>Fired after a channel is created (via dialog, clone, or discovered by Reload()).</summary>
    public event Action<ChannelConfig>? ChannelAdded;

    /// <summary>Fired after a channel's config changes in place (old, new) — filename/key is unchanged.</summary>
    public event Action<ChannelConfig, ChannelConfig>? ChannelUpdated;

    /// <summary>Fired after a channel is deleted or disappears from disk (picked up by Reload()).</summary>
    public event Action<ChannelConfig>? ChannelRemoved;

    public IReadOnlyList<ChannelConfig> Channels
    {
        get { lock (_lock) { return _channels.ToList(); } }
    }

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

        lock (_lock)
        {
            _channels.Clear();
            foreach (var config in _loader.LoadAll(_configDir))
            {
                _channels.Add(config);
                using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = config.Name }))
                {
                    _logger.LogInformation("Канал загружен из {File}", config.Filename);
                }
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

    public bool NameExists(string name, string? excludeFilename = null)
    {
        lock (_lock)
        {
            return _channels.Any(c => c.Filename != excludeFilename && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public ChannelConfig Add(ChannelConfig config)
    {
        lock (_lock)
        {
            config.Filename = GenerateFilenameLocked(config.Name);
            _loader.Save(_configDir, config);
            _channels.Add(config);
            using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = config.Name }))
                _logger.LogInformation("Канал создан ({File})", config.Filename);
        }
        ChannelAdded?.Invoke(config);
        return config;
    }

    public ChannelConfig Update(string filename, ChannelConfig updated)
    {
        ChannelConfig old;
        lock (_lock)
        {
            var index = _channels.FindIndex(c => c.Filename == filename);
            if (index < 0) throw new InvalidOperationException($"Канал с файлом '{filename}' не найден");
            old = _channels[index];

            updated.Filename = filename;
            _loader.Save(_configDir, updated);
            _channels[index] = updated;
            using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = updated.Name }))
                _logger.LogInformation("Канал обновлён ({File})", filename);
        }
        ChannelUpdated?.Invoke(old, updated);
        return updated;
    }

    public ChannelConfig Clone(string filename)
    {
        ChannelConfig clone;
        lock (_lock)
        {
            var source = _channels.FirstOrDefault(c => c.Filename == filename)
                ?? throw new InvalidOperationException($"Канал с файлом '{filename}' не найден");
            clone = _loader.Clone(source);
            clone.Name = MakeUniqueNameLocked(source.Name + " (копия)");
            clone.Filename = GenerateFilenameLocked(clone.Name);
            clone.AutoStart = false; // don't race two engines against the same source right after cloning
            _loader.Save(_configDir, clone);
            _channels.Add(clone);
            using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = clone.Name }))
                _logger.LogInformation("Канал клонирован из '{Source}' ({File})", source.Name, clone.Filename);
        }
        ChannelAdded?.Invoke(clone);
        return clone;
    }

    public void Delete(string filename)
    {
        ChannelConfig removed;
        lock (_lock)
        {
            var index = _channels.FindIndex(c => c.Filename == filename);
            if (index < 0) return;
            removed = _channels[index];
            _channels.RemoveAt(index);
            var path = Path.Combine(_configDir, filename);
            using (_logger.BeginScope(new Dictionary<string, object> { ["Channel"] = removed.Name }))
            {
                try { File.Delete(path); }
                catch (IOException ex) { _logger.LogWarning(ex, "Не удалось удалить файл конфига {File}", filename); }
                _logger.LogInformation("Канал удалён ({File})", filename);
            }
        }
        ChannelRemoved?.Invoke(removed);
    }

    /// <summary>Re-scans configDir from disk and diffs against the in-memory list, firing Added/Updated/Removed for real changes.</summary>
    public readonly record struct ReloadResult(int Added, int Updated, int Removed, string? Error = null);

    public ReloadResult Reload()
    {
        List<ChannelConfig> fresh;
        try
        {
            fresh = _loader.LoadAll(_configDir).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обновления конфигурации");
            return new ReloadResult(0, 0, 0, ex.Message);
        }

        var added = new List<ChannelConfig>();
        var removed = new List<ChannelConfig>();
        var updated = new List<(ChannelConfig Old, ChannelConfig New)>();

        lock (_lock)
        {
            var oldByFile = _channels.ToDictionary(c => c.Filename);
            var newByFile = fresh.ToDictionary(c => c.Filename);

            foreach (var (file, cfg) in oldByFile)
                if (!newByFile.ContainsKey(file))
                    removed.Add(cfg);

            foreach (var (file, cfg) in newByFile)
            {
                if (!oldByFile.TryGetValue(file, out var old))
                    added.Add(cfg);
                else if (_loader.Serialize(old) != _loader.Serialize(cfg))
                    updated.Add((old, cfg));
            }

            _channels.Clear();
            _channels.AddRange(fresh);
        }

        _logger.LogInformation("Конфигурация обновлена: {Added} новых, {Updated} изменено, {Removed} удалено",
            added.Count, updated.Count, removed.Count);

        foreach (var cfg in removed) ChannelRemoved?.Invoke(cfg);
        foreach (var (old, cfg) in updated) ChannelUpdated?.Invoke(old, cfg);
        foreach (var cfg in added) ChannelAdded?.Invoke(cfg);

        return new ReloadResult(added.Count, updated.Count, removed.Count);
    }

    private string GenerateFilenameLocked(string name) =>
        GenerateFilename(name, _channels.Select(c => c.Filename));

    private string MakeUniqueNameLocked(string baseName) =>
        MakeUniqueName(baseName, _channels.Select(c => c.Name));

    internal static string GenerateFilename(string name, IEnumerable<string> existingFilenames)
    {
        var baseName = SanitizeFilenamePart(name);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "channel";

        var existing = new HashSet<string>(existingFilenames, StringComparer.OrdinalIgnoreCase);
        var candidate = baseName + ".yaml";
        var n = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{baseName} ({n}).yaml";
            n++;
        }
        return candidate;
    }

    internal static string MakeUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;
        var n = 2;
        while (existing.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    internal static string SanitizeFilenamePart(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
