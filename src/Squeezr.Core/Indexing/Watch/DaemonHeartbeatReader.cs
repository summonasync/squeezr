using System.Diagnostics;
using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

public static class DaemonHeartbeatReader
{
    public static DaemonHeartbeat? TryRead(string storeDir, string kind = DaemonHeartbeat.DefaultKind) =>
        TryReadFile(DaemonHeartbeat.PathFor(storeDir, kind), kind);

    // A store answers with every process it holds: the per-pid beats, plus the per-kind heartbeat of a daemon
    // old enough not to write them. A beat is keyed by (kind, pid), so the owner appears once from either shape,
    // and a beat whose body contradicts its own filename is dropped rather than trusted.
    public static IReadOnlyList<DaemonHeartbeat> TryReadAll(string storeDir)
    {
        var byProcess = new Dictionary<string, DaemonHeartbeat>(StringComparer.Ordinal);
        foreach (var path in EnumerateBeatFiles(storeDir))
        {
            if (!DaemonHeartbeat.TryParseBeatFileName(Path.GetFileName(path), out var kind, out var filePid))
            {
                continue;
            }

            var beat = TryReadFile(path, kind);
            if (beat is null || beat.Pid != filePid)
            {
                continue;
            }

            Merge(byProcess, beat);
        }

        foreach (var kind in (string[])[DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind])
        {
            Merge(byProcess, TryRead(storeDir, kind));
        }

        return [.. byProcess.Values
            .OrderBy(beat => beat.Kind, StringComparer.Ordinal)
            .ThenBy(beat => beat.Pid)];
    }

    public static bool IsAlive(
        DaemonHeartbeat heartbeat,
        DateTime utcNow,
        Func<int, bool>? isProcessAlive = null)
    {
        if (utcNow - heartbeat.LastBeatAt > DaemonHeartbeat.StaleThreshold)
        {
            return false;
        }
        return (isProcessAlive ?? IsProcessAlive)(heartbeat.Pid);
    }

    // A zombie still has a /proc entry, so process presence alone would keep a killed owner's claim and
    // lock alive until the parent reaps it — on Linux the kernel's own process state is the verdict, and
    // state 'Z' is dead for every claim and beat rule. Platforms without /proc keep the presence probe.
    public static bool IsProcessAlive(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return IsKnownProcess(pid);
        }

        return ProcStat.TryReadState(pid) is { } state
            ? state != ProcStat.ZombieState
            : IsKnownProcess(pid);
    }

    private static bool IsKnownProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static DaemonHeartbeat? TryReadFile(string path, string? kind)
    {
        try
        {
            var heartbeat = JsonSerializer.Deserialize<DaemonHeartbeat>(File.ReadAllText(path), SqueezrJson.FileOptions);
            if (heartbeat is not { Pid: > 0 } parsed)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(parsed.Kind) && kind is not null
                ? parsed with { Kind = kind }
                : parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateBeatFiles(string storeDir)
    {
        try
        {
            return Directory.EnumerateFiles(DaemonHeartbeat.BeatsDirFor(storeDir)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Merge(Dictionary<string, DaemonHeartbeat> byProcess, DaemonHeartbeat? heartbeat)
    {
        if (heartbeat is null || string.IsNullOrWhiteSpace(heartbeat.Kind))
        {
            return;
        }

        var key = $"{heartbeat.Kind}\n{heartbeat.Pid}";
        if (!byProcess.TryGetValue(key, out var held) || heartbeat.LastBeatAt > held.LastBeatAt)
        {
            byProcess[key] = heartbeat;
        }
    }
}
