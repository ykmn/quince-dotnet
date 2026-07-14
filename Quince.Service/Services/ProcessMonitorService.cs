using System.Collections.Concurrent;
using System.Diagnostics;

namespace Quince.Service.Services;

/// <summary>One row of <see cref="ProcessMonitorService.Snapshot"/> — either the app's own main
/// process (<see cref="ChannelName"/>/<see cref="Role"/> both null) or one of its tracked ffmpeg
/// subprocesses (<see cref="Role"/> is "capture" or "writer", see
/// <see cref="AudioEngineManager.GetTrackedProcesses"/>). Left unformatted (not a single display
/// string) so the Razor dialog can localize the "захват"/"запись файла" role text itself, same as
/// every other UI-facing label in this app.</summary>
public record ProcessUsageRow(string? ChannelName, string? Role, int Pid, string ProcessName, double? CpuPercent, long MemoryBytes);

/// <summary>
/// Backs the admin "Монитор ресурсов" dialog: CPU%/memory for the main <c>Quince.Service.exe</c>
/// process plus every ffmpeg subprocess currently spawned by a running channel (capture and/or
/// output writer, tracked via <see cref="AudioEngineManager.GetTrackedProcesses"/> rather than a
/// process-tree/WMI enumeration — this app already knows exactly which PIDs it spawned and for what,
/// so there's no need for a broader OS-level scan).
/// </summary>
public class ProcessMonitorService
{
    private readonly AudioEngineManager _engineManager;

    // Keyed by PID: the previous sample's (wall-clock time, cumulative CPU time) so CPU% can be
    // computed as a delta over the interval between two Snapshot() calls, the same technique Task
    // Manager itself uses — a single point-in-time read of TotalProcessorTime is a cumulative total
    // since process start, not a rate. A PID with no prior sample yet (first time seen, or the
    // previous sample was evicted because the process had exited) reports CpuPercent: null rather
    // than 0, so the UI can show "…" instead of a misleading zero for that one row on the first tick.
    private readonly ConcurrentDictionary<int, (DateTime SampledAt, TimeSpan CpuTime)> _lastSample = new();

    public ProcessMonitorService(AudioEngineManager engineManager)
    {
        _engineManager = engineManager;
    }

    public IReadOnlyList<ProcessUsageRow> Snapshot()
    {
        var rows = new List<ProcessUsageRow>();
        var seenPids = new HashSet<int>();

        var main = Process.GetCurrentProcess();
        seenPids.Add(main.Id);
        if (TryBuildRow(null, null, main, out var mainRow)) rows.Add(mainRow);

        foreach (var (channelName, role, pid) in _engineManager.GetTrackedProcesses())
        {
            if (!seenPids.Add(pid)) continue; // capture+writer sharing a PID shouldn't happen, but don't double-count if it ever does
            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch (ArgumentException) { continue; } // already exited between the PID being read and this lookup

            if (TryBuildRow(channelName, role, proc, out var row)) rows.Add(row);
        }

        EvictStaleSamples(seenPids);
        return rows;
    }

    private bool TryBuildRow(string? channelName, string? role, Process process, out ProcessUsageRow row)
    {
        try
        {
            process.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;
            double? cpuPercent = null;
            if (_lastSample.TryGetValue(process.Id, out var previous))
            {
                var elapsed = (now - previous.SampledAt).TotalSeconds;
                if (elapsed > 0)
                {
                    var cpuDelta = (cpuTime - previous.CpuTime).TotalSeconds;
                    cpuPercent = Math.Max(0, cpuDelta / elapsed / Environment.ProcessorCount * 100.0);
                }
            }
            _lastSample[process.Id] = (now, cpuTime);

            row = new ProcessUsageRow(channelName, role, process.Id, process.ProcessName, cpuPercent, process.WorkingSet64);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process exited between GetProcessById/GetCurrentProcess and this read — just skip it
            // for this tick, same as the ArgumentException case in Snapshot() above.
            row = null!;
            return false;
        }
    }

    private void EvictStaleSamples(HashSet<int> seenPids)
    {
        foreach (var pid in _lastSample.Keys)
            if (!seenPids.Contains(pid))
                _lastSample.TryRemove(pid, out _);
    }
}
