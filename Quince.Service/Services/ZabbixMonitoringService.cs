using System.Collections.Concurrent;
using System.Text.Json;
using Quince.Service.Audio;

namespace Quince.Service.Services;

public sealed class ZabbixMonitoringService : BackgroundService
{
    private readonly ChannelManager _channels;
    private readonly AudioEngineManager _engines;
    private readonly ILogger<ZabbixMonitoringService> _logger;
    private readonly MonitoringDiskCache _disk = new();
    private readonly ConcurrentDictionary<string, (LevelReading Reading, DateTimeOffset At)> _levels = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private string? _snapshot;
    public string? Snapshot => Volatile.Read(ref _snapshot);

    public ZabbixMonitoringService(ChannelManager channels, AudioEngineManager engines, ILogger<ZabbixMonitoringService> logger)
    {
        _channels = channels;
        _engines = engines;
        _logger = logger;
        _engines.LevelUpdated += OnLevel;
        _engines.StatusUpdated += OnStatus;
    }

    private void OnLevel(string name, LevelReading level) => _levels[name] = (level, DateTimeOffset.UtcNow);
    private void OnStatus(string name, EngineStatus status)
    {
        if (!status.IsRecording) _levels.TryRemove(name, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not hold up Windows service startup on engine locks or disk enumeration.
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    var configs = _channels.Channels;
                    var now = DateTimeOffset.UtcNow;
                    var sizes = _disk.Refresh(configs.Select(c => c.SavePath), now, stoppingToken);
                    var rows = configs.Select(c =>
                    {
                        var state = _engines.GetMonitoringState(c.Name);
                        _levels.TryGetValue(c.Name, out var level);
                        sizes.TryGetValue(c.SavePath, out var size);
                        return ChannelTelemetry.Create(c, state.Status, state.MetadataReceivedAt,
                            level.Reading, level.Reading == null ? null : level.At, size, now);
                    }).ToArray();
                    // Duplicate manually-copied IDs must fail visibly, never alias channels in LLD.
                    if (rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
                        throw new InvalidOperationException("Duplicate monitoring_id in channel configuration");
                    var names = configs.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var name in _levels.Keys.Where(n => !names.Contains(n))) _levels.TryRemove(name, out _);
                    Volatile.Write(ref _snapshot, JsonSerializer.Serialize(new
                    {
                        SchemaVersion = 1, GeneratedAt = now.ToUnixTimeSeconds(), Channels = rows
                    }, JsonOptions));
                }
                catch (Exception ex)
                {
                    // Preserve timestamp of the last good snapshot; Zabbix detects stalled collection.
                    _logger.LogWarning(ex, "Не удалось обновить снимок мониторинга Zabbix");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        _engines.LevelUpdated -= OnLevel;
        _engines.StatusUpdated -= OnStatus;
        base.Dispose();
    }
}
