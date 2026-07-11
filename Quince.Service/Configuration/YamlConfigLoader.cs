using System.Text.RegularExpressions;
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
                MigrateFileDurationSeconds(config, text);
            }
            catch (Exception ex)
            {
                LogLoadError(file, ex);
            }
            if (config != null && !string.IsNullOrWhiteSpace(config.Name))
                yield return config;
        }
    }

    /// <summary>
    /// `file_duration_seconds` was renamed to `file_duration_minutes`. Old configs still on disk with
    /// the former key would otherwise silently lose their configured rotation interval — deserializing
    /// ignores the unmatched old key (see <c>IgnoreUnmatchedProperties</c>) and <see cref="ChannelConfig.FileDurationMinutes"/>
    /// would just sit at its default instead of the value the file actually specified. Only
    /// applies when the new key isn't already present (a resaved/hand-written file takes priority).
    /// </summary>
    private static void MigrateFileDurationSeconds(ChannelConfig config, string rawYaml)
    {
        if (Regex.IsMatch(rawYaml, @"^\s*file_duration_minutes\s*:", RegexOptions.Multiline))
            return;

        var match = Regex.Match(rawYaml, @"^\s*file_duration_seconds\s*:\s*(\d+)", RegexOptions.Multiline);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var seconds))
            config.FileDurationMinutes = Math.Max(1, seconds / 60);
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
        WriteTextClearingReadOnly(path, text);
    }

    /// <summary>Plain <see cref="File.WriteAllText(string,string)"/> throws
    /// <see cref="UnauthorizedAccessException"/> not just for a genuine ACL denial but also for the
    /// much more mundane case of the target file having the Windows "read-only" attribute set (common
    /// after copying from a zip/network share/backup, or a prior manual edit in an editor that
    /// preserves it) — clear that attribute first so a stale flag doesn't masquerade as a permissions
    /// problem. A real ACL/Controlled-Folder-Access denial still throws through normally; this only
    /// removes the one cause that's both common and trivially fixable in-process.</summary>
    private static void WriteTextClearingReadOnly(string path, string text)
    {
        if (File.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
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
        var path = Path.Combine(configDir, "settings.yaml");
        var text = _serializer.Serialize(config);
        WriteTextClearingReadOnly(path, text);
    }

    /// <summary>settings.yaml was previously named app.yaml — on an already-deployed instance, rename
    /// the old file in place the first time it's found so upgrades don't lose existing settings.</summary>
    private static void MigrateLegacyAppYaml(string configDir)
    {
        var oldPath = Path.Combine(configDir, "app.yaml");
        var newPath = Path.Combine(configDir, "settings.yaml");
        if (File.Exists(oldPath) && !File.Exists(newPath))
            File.Move(oldPath, newPath);
    }

    public AppConfig LoadApp(string configDir)
    {
        MigrateLegacyAppYaml(configDir);
        var path = Path.Combine(configDir, "settings.yaml");
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

    /// <summary>Matches a line whose key is "local" or "ldap" in any casing (apricot2 itself writes
    /// "Local:"/"LDAP:" while every other key in the file is lowercase-underscored) — used to
    /// normalize just those two keys to lowercase before deserializing, since the shared deserializer
    /// below matches YAML keys case-sensitively against the (already-lowercase) property names.</summary>
    private static readonly Regex LocalLdapKeyLine = new(@"^(\s*)(local|ldap)(\s*:)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public LdapConfig LoadLdapConfig(string configDir)
    {
        var path = Path.Combine(configDir, "ldap.yaml");
        if (!File.Exists(path))
            return new LdapConfig { Present = false };

        try
        {
            var text = File.ReadAllText(path);
            text = LocalLdapKeyLine.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant() + m.Groups[3].Value);
            var cfg = _deserializer.Deserialize<LdapConfig>(text) ?? new LdapConfig();
            cfg.Present = true;
            return cfg;
        }
        catch (Exception ex)
        {
            LogLoadError(path, ex);
            return new LdapConfig { Present = false };
        }
    }

    public UsersConfig LoadUsers(string configDir)
    {
        var path = Path.Combine(configDir, "users.yaml");
        if (!File.Exists(path))
            return new UsersConfig();

        try
        {
            var text = File.ReadAllText(path);
            return _deserializer.Deserialize<UsersConfig>(text) ?? new UsersConfig();
        }
        catch (Exception ex)
        {
            LogLoadError(path, ex);
            return new UsersConfig();
        }
    }

    public SecretConfig LoadSecrets(string configDir)
    {
        var path = Path.Combine(configDir, "secret.yaml");
        if (!File.Exists(path))
            return new SecretConfig();

        try
        {
            var text = File.ReadAllText(path);
            return _deserializer.Deserialize<SecretConfig>(text) ?? new SecretConfig();
        }
        catch (Exception ex)
        {
            LogLoadError(path, ex);
            return new SecretConfig();
        }
    }

    public SessionsFile LoadSessions(string configDir)
    {
        var path = Path.Combine(configDir, "sessions.yaml");
        if (!File.Exists(path))
            return new SessionsFile();

        try
        {
            var text = File.ReadAllText(path);
            return _deserializer.Deserialize<SessionsFile>(text) ?? new SessionsFile();
        }
        catch (Exception ex)
        {
            LogLoadError(path, ex);
            return new SessionsFile();
        }
    }

    /// <summary>Best-effort — session persistence is a convenience (survive a service restart), not a
    /// source of truth, so a transient write failure (e.g. file briefly locked) just logs and moves on
    /// rather than crashing whatever login/logout request triggered the save.</summary>
    public void SaveSessions(string configDir, SessionsFile sessions)
    {
        try
        {
            var path = Path.Combine(configDir, "sessions.yaml");
            var text = _serializer.Serialize(sessions);
            WriteTextClearingReadOnly(path, text);
        }
        catch (Exception ex)
        {
            LogLoadError(Path.Combine(configDir, "sessions.yaml"), ex);
        }
    }

    public LivewireCacheFile LoadLivewireCache(string configDir)
    {
        var path = Path.Combine(configDir, "livewire.yaml");
        if (!File.Exists(path))
            return new LivewireCacheFile();

        try
        {
            var text = File.ReadAllText(path);
            return _deserializer.Deserialize<LivewireCacheFile>(text) ?? new LivewireCacheFile();
        }
        catch (Exception ex)
        {
            LogLoadError(path, ex);
            return new LivewireCacheFile();
        }
    }

    /// <summary>Best-effort, same as <see cref="SaveSessions"/> — a stale/unwritable cache just means
    /// the picker starts empty next run, not a reason to disrupt whatever UI action triggered the save.</summary>
    public void SaveLivewireCache(string configDir, LivewireCacheFile cache)
    {
        try
        {
            var path = Path.Combine(configDir, "livewire.yaml");
            var text = _serializer.Serialize(cache);
            WriteTextClearingReadOnly(path, text);
        }
        catch (Exception ex)
        {
            LogLoadError(Path.Combine(configDir, "livewire.yaml"), ex);
        }
    }
}
