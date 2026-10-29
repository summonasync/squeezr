using System.Diagnostics;
using System.Text.Json;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Output;

public static class FleetWatchdProbe
{
    public static FleetWatchdState Probe() =>
        Probe(WatchdPaths.LockPath, DaemonHeartbeatReader.IsProcessAlive);

    internal static FleetWatchdState Probe(string lockPath, Func<int, bool> isProcessAlive)
    {
        WatchdLockFile? owner;
        try
        {
            owner = JsonSerializer.Deserialize<WatchdLockFile>(File.ReadAllText(lockPath), SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FleetWatchdState(false, null);
        }

        if (owner is null || owner.Pid <= 0)
        {
            return new FleetWatchdState(false, null);
        }

        var pid = owner.Pid;

        if (!string.Equals(owner.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return new FleetWatchdState(true, pid);
        }

        if (!isProcessAlive(pid))
        {
            return new FleetWatchdState(false, null);
        }

        return StartedAtMatches(pid, owner.StartedAtUtc)
            ? new FleetWatchdState(true, pid)
            : new FleetWatchdState(false, null);
    }

    private static bool StartedAtMatches(int pid, DateTime startedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return (process.StartTime.ToUniversalTime() - startedAtUtc).Duration() <= WatchdStartTolerance;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static readonly TimeSpan WatchdStartTolerance = TimeSpan.FromSeconds(2);

    private sealed record WatchdLockFile(int Pid, string? Host, DateTime StartedAtUtc);
}
