using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public static class FleetListing
{
    public static IReadOnlyList<FleetEntry> Load()
    {
        var now = DateTime.UtcNow;
        var staleKeys = OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .ToHashSet(StringComparer.Ordinal);
        var entries = WorkspaceRegistry.Load();
        var collidingNames = CollidingNames.SharedByLiveEntries(entries);
        return entries
            .Select(entry => BuildEntry(entry, now, staleKeys, collidingNames.Contains(entry.Name)))
            .OrderByDescending(entry => entry.Daemons.Any(daemon => daemon.IsAlive))
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
    }

    // The roster groups by kind so each table cell reads top-down: the claim owner first, then its observers,
    // then any process whose beat outlived it. Alive is the same verdict the per-kind heartbeat has always used
    // (fresh beat AND a pid this machine can see); who runs the watch loop is the claim marker's answer alone,
    // and nothing here decides ownership on its own.
    internal static IReadOnlyList<FleetDaemonProcess> ProbeDaemons(
        string storeDir,
        DateTime now,
        Func<int, bool>? isProcessAlive = null)
    {
        var owner = WatchOwnerMarker.TryRead(storeDir);
        return [.. DaemonHeartbeatReader.TryReadAll(storeDir)
            .Select(beat => Describe(beat, owner, now, isProcessAlive))
            .OrderBy(daemon => daemon.Kind, StringComparer.Ordinal)
            .ThenByDescending(daemon => daemon.Owner)
            .ThenByDescending(daemon => daemon.IsAlive)
            .ThenBy(daemon => daemon.Pid)];
    }

    private static FleetEntry BuildEntry(
        WorkspaceRegistryEntry entry,
        DateTime now,
        IReadOnlySet<string> staleKeys,
        bool nameCollision)
    {
        var storeDir = SqueezrPaths.StoreDir(entry.RootPath);
        using var meta = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(entry.RootPath));
        return new FleetEntry(
            entry.Name,
            entry.RootPath,
            entry.StoreKey,
            ProbeDaemon(storeDir, DaemonHeartbeat.DefaultKind, now),
            ProbeDaemon(storeDir, DaemonHeartbeat.ServeWatchKind, now),
            ProbeDaemons(storeDir, now),
            Stale: staleKeys.Contains(entry.StoreKey),
            PendingChunks: meta?.CountPendingChunks(null),
            LastIndexedAt: meta?.GetLatestFullIndexAt(),
            NameCollision: nameCollision ? true : null);
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

    private static FleetDaemonProcess Describe(
        DaemonHeartbeat beat,
        WatchOwnerMarker? owner,
        DateTime now,
        Func<int, bool>? isProcessAlive)
    {
        var alive = isProcessAlive is null
            ? DaemonHeartbeatReader.IsAlive(beat, now)
            : DaemonHeartbeatReader.IsAlive(beat, now, isProcessAlive);
        return new FleetDaemonProcess(
            beat.Kind,
            beat.Pid,
            alive ? FleetDaemonState.AliveStatus : FleetDaemonState.DeadStatus,
            alive ? (long)(now - beat.StartedAt).TotalSeconds : null,
            owner is not null && owner.OwnsBeat(beat),
            beat.Host);
    }
}
