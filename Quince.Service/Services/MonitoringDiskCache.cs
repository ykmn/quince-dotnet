namespace Quince.Service.Services;

/// <summary>At most two directory enumerations, including timed-out OS I/O. Never starts another
/// scan for a path while its old scan is still pending. Called only by the snapshot worker.</summary>
internal sealed class MonitoringDiskCache
{
    private sealed record Scan(Task<FolderUsage?> Task, DateTimeOffset StartedAt, FolderUsage? Previous);
    private readonly Dictionary<string, Scan> _scans = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, FolderUsage?> _scan;

    internal MonitoringDiskCache(Func<string, CancellationToken, FolderUsage?>? scan = null)
        => _scan = scan ?? ScanFolder;

    public IReadOnlyDictionary<string, FolderUsage?> Refresh(IEnumerable<string> paths, DateTimeOffset now,
        CancellationToken ct)
    {
        var requested = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _scans.Keys.Where(k => !requested.Contains(k) && _scans[k].Task.IsCompleted).ToArray())
            _scans.Remove(key);
        var active = _scans.Values.Count(s => !s.Task.IsCompleted);
        // Oldest attempts first so hundreds of channels cannot starve behind the first two.
        foreach (var path in requested.OrderBy(p => _scans.TryGetValue(p, out var s) ? s.StartedAt : DateTimeOffset.MinValue))
        {
            if (active >= 2) break;
            if (_scans.TryGetValue(path, out var old) &&
                (!old.Task.IsCompleted || now - old.StartedAt < TimeSpan.FromMinutes(5))) continue;
            var previous = old?.Task.IsCompletedSuccessfully == true ? old.Task.Result : old?.Previous;
            _scans[path] = new(Task.Run(() => _scan(path, ct), CancellationToken.None), now, previous);
            active++;
        }
        return requested.ToDictionary(p => p, p =>
        {
            if (!_scans.TryGetValue(p, out var scan)) return null;
            return scan.Task.IsCompletedSuccessfully ? scan.Task.Result : scan.Previous;
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static FolderUsage? ScanFolder(string path, CancellationToken ct)
    {
        try
        {
            long bytes = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                bytes = checked(bytes + file.Length);
            }
            return new(bytes, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or OperationCanceledException or OverflowException)
        {
            return null;
        }
    }
}
