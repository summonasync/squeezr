using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public static class FleetListing
{
    public static IReadOnlyList<FleetEntry> Load() => Load(DefaultSampler);

    internal static IReadOnlyList<FleetEntry> Load(FleetResourceSampler sampleResources)
    {
        var now = DateTime.UtcNow;
        var staleKeys = OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .ToHashSet(StringComparer.Ordinal);
        var entries = WorkspaceRegistry.Load();
        var collidingNames = CollidingNames.SharedByLiveEntries(entries);
        var rosters = entries
            .Select(entry => ProbeDaemons(SqueezrPaths.StoreDir(entry), now))
            .ToList();
        var samples = SampleOnce(rosters, sampleResources);
        return entries
            .Select((entry, index) => BuildEntry(
                entry, now, staleKeys, collidingNames.Contains(entry.Name), rosters[index], samples))
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

    // One measurement pass for the whole fleet, not one per row: every live pid of every store goes into a
    // single shared sampling window, so the added wall time stays bounded no matter how many stores answer.
    internal static IReadOnlyDictionary<int, DaemonResourceSample> SampleOnce(
        IReadOnlyCollection<IReadOnlyList<FleetDaemonProcess>> rosters,
        FleetResourceSampler sampleResources)
    {
        var pids = LivePids(rosters);
        return pids.Count == 0
            ? new Dictionary<int, DaemonResourceSample>()
            : sampleResources(pids, DaemonResourceSampler.Window);
    }

    internal static FleetDaemonProcess StampResources(
        FleetDaemonProcess daemon,
        IReadOnlyDictionary<int, DaemonResourceSample> samples) =>
        daemon.IsAlive && samples.TryGetValue(daemon.Pid, out var sample)
            ? daemon with { CpuPercent = sample.CpuPercent, RssBytes = sample.RssBytes }
            : daemon;

    private static IReadOnlyCollection<int> LivePids(
        IEnumerable<IReadOnlyList<FleetDaemonProcess>> rosters) =>
        rosters.SelectMany(roster => roster)
            .Where(daemon => daemon.IsAlive)
            .Select(daemon => daemon.Pid)
            .Distinct()
            .ToList();

    private static IReadOnlyDictionary<int, DaemonResourceSample> DefaultSampler(
        IReadOnlyCollection<int> pids,
        TimeSpan window) =>
        DaemonResourceSampler.Sample(pids, window);

    private static FleetEntry BuildEntry(
        WorkspaceRegistryEntry entry,
        DateTime now,
        IReadOnlySet<string> staleKeys,
        bool nameCollision,
        IReadOnlyList<FleetDaemonProcess> daemons,
        IReadOnlyDictionary<int, DaemonResourceSample> samples)
    {
        var storeDir = SqueezrPaths.StoreDir(entry);
        using var meta = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(entry));
        return new FleetEntry(
            entry.Name,
            entry.RootPath,
            entry.StoreKey,
            ProbeDaemon(storeDir, DaemonHeartbeat.DefaultKind, now),
            ProbeDaemon(storeDir, DaemonHeartbeat.ServeWatchKind, now),
            daemons.Select(daemon => StampResources(daemon, samples)).ToList(),
            Stale: staleKeys.Contains(entry.StoreKey),
            PendingChunks: meta?.CountPendingChunks(null),
            LastIndexedAt: meta?.GetLatestFullIndexAt(),
            NameCollision: nameCollision ? true : null,
            Adoption: FleetWatchAdoption.FromStore(entry));
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

        // Freshness honesty: a beat past the stale window or naming a gone pid says nothing about work in
        // flight — its last-known activity must never reach the table or --json as if it were live.
        var activity = alive ? beat.Activity : null;
        return new FleetDaemonProcess(
            beat.Kind,
            beat.Pid,
            alive ? FleetDaemonState.AliveStatus : FleetDaemonState.DeadStatus,
            alive ? (long)(now - beat.StartedAt).TotalSeconds : null,
            owner is not null && owner.OwnsBeat(beat),
            beat.Host,
            activity?.Phase,
            activity?.Repo,
            activity?.DoneItems,
            activity?.TotalItems,
            activity is null ? null : (long)(now - activity.PhaseStartedAt).TotalSeconds,
            activity?.Provider,
            activity?.Executor);
    }
}
