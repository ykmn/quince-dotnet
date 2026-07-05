namespace Quince.Service.Configuration;

public static class PathResolver
{
    public static string Resolve(string? configuredValue, string defaultRelative)
    {
        var value = configuredValue ?? defaultRelative;
        return Path.IsPathRooted(value) ? value : Path.Combine(AppContext.BaseDirectory, value);
    }
}
