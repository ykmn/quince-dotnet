using Quince.Service.Configuration;
using Xunit;

namespace Quince.Service.Tests.Configuration;

public class YamlConfigLoaderTests
{
    private static string CreateTempConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quince-yaml-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void LoadAll_LegacyFileDurationSeconds_MigratesToMinutes()
    {
        var dir = CreateTempConfigDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Legacy.yaml"), """
                name: Legacy
                file_duration_seconds: 600
                """);

            var loader = new YamlConfigLoader();
            var config = loader.LoadAll(dir).Single();

            Assert.Equal(10, config.FileDurationMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_NewFileDurationMinutes_UsedAsIs()
    {
        var dir = CreateTempConfigDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "New.yaml"), """
                name: New
                file_duration_minutes: 15
                """);

            var loader = new YamlConfigLoader();
            var config = loader.LoadAll(dir).Single();

            Assert.Equal(15, config.FileDurationMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_BothKeysPresent_NewKeyWins()
    {
        var dir = CreateTempConfigDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Both.yaml"), """
                name: Both
                file_duration_seconds: 600
                file_duration_minutes: 20
                """);

            var loader = new YamlConfigLoader();
            var config = loader.LoadAll(dir).Single();

            Assert.Equal(20, config.FileDurationMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_NoDurationKey_UsesDefault()
    {
        var dir = CreateTempConfigDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Default.yaml"), """
                name: Default
                """);

            var loader = new YamlConfigLoader();
            var config = loader.LoadAll(dir).Single();

            Assert.Equal(10, config.FileDurationMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveLivewireCache_ThenLoad_RoundTrips()
    {
        var dir = CreateTempConfigDir();
        try
        {
            var loader = new YamlConfigLoader();
            var cache = new LivewireCacheFile
            {
                Channels =
                {
                    new LivewireCacheEntry { Number = 1, Name = "Novoe Expres", DeviceName = "lwwd", DeviceIp = "172.22.0.49", LastSeen = "2026-07-13 10:46:01" },
                    new LivewireCacheEntry { Number = 10, Name = "", DeviceName = "", DeviceIp = "", LastSeen = "2026-07-13 10:46:02" },
                },
            };

            loader.SaveLivewireCache(dir, cache);
            var loaded = loader.LoadLivewireCache(dir);

            Assert.Equal(2, loaded.Channels.Count);
            Assert.Contains(loaded.Channels, c => c.Number == 1 && c.Name == "Novoe Expres" && c.DeviceName == "lwwd" && c.DeviceIp == "172.22.0.49");
            Assert.Contains(loaded.Channels, c => c.Number == 10 && c.Name == "" && c.DeviceName == "" && c.DeviceIp == "");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadLivewireCache_MissingFile_ReturnsEmpty()
    {
        var dir = CreateTempConfigDir();
        try
        {
            var loader = new YamlConfigLoader();
            var loaded = loader.LoadLivewireCache(dir);

            Assert.Empty(loaded.Channels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
