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

            Assert.Equal(60, config.FileDurationMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
