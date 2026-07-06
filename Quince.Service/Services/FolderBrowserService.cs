namespace Quince.Service.Services;

/// <summary>Read-only server-side directory browsing for the channel edit dialog's "Обзор…" button — a
/// real native OS folder picker isn't viable here since this is a Windows-Service-hosted web app (no
/// interactive desktop session to show one in).</summary>
public class FolderBrowserService
{
    public sealed record Entry(string Name, string FullPath);

    public IReadOnlyList<Entry> GetDrives()
    {
        var result = new List<Entry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            try { result.Add(new Entry(drive.Name, drive.RootDirectory.FullName)); }
            catch (IOException) { /* skip drives that stop responding mid-enumeration */ }
        }
        return result;
    }

    /// <summary>Lists subfolders of <paramref name="path"/>, or drives if it's null/empty. Folders that
    /// fail to enumerate (permissions, transient I/O) are skipped rather than aborting the whole listing.</summary>
    public IReadOnlyList<Entry> ListSubfolders(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return GetDrives();

        var result = new List<Entry>();
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            try { result.Add(new Entry(Path.GetFileName(dir), dir)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? GetParent(string path)
    {
        try { return Directory.GetParent(path)?.FullName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
