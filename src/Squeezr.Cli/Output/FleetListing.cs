using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public static class FleetListing
{
    public static IReadOnlyList<FleetEntry> Load()
    {
        var now = DateTime.UtcNow;
        return WorkspaceRegistry.Load()
            .Select(entry => BuildEntry(entry, now))
            .OrderByDescending(entry => entry.Watch.IsAlive || entry.ServeWatch.IsAlive)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static FleetEntry BuildEntry(WorkspaceRegistryEntry entry, DateTime now)
    {
        var storeDir = SqueezrPaths.StoreDir(entry.RootPath);
        using var meta = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(entry.RootPath));
        return new FleetEntry(
            entry.Name,
            entry.RootPath,
            entry.StoreKey,
            ProbeDaemon(storeDir, DaemonHeartbeat.DefaultKind, now),
            ProbeDaemon(storeDir, DaemonHeartbeat.ServeWatchKind, now),
            Stale: !Directory.Exists(entry.RootPath),
            PendingChunks: meta?.CountPendingChunks(null),
            LastIndexedAt: meta?.GetLatestFullIndexAt());
    }

    private static FleetDaemonState ProbeDaemon(string storeDir, string kind, DateTime now)
    {
        var heartbeat = DaemonHeartbeatReader.TryRead(storeDir, kind);
        if (heartbeat is null)
        {
            return FleetDaemonState.None;
        }

        if (!DaemonHeartbeatReader.IsAlive(heartbeat, now))
        {
            return FleetDaemonState.Dead(heartbeat.Pid);
        }

        return FleetDaemonState.Alive(heartbeat.Pid, (long)(now - heartbeat.StartedAt).TotalSeconds);
    }
}
