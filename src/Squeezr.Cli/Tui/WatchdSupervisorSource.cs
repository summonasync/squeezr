using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public sealed class WatchdSupervisorSource : IWatchdSupervisorSource
{
    private readonly Func<IReadOnlyList<WorkspaceRegistryEntry>> _registry;
    private readonly SupervisionState _state;
    private readonly WatchdDaemonProbe _daemon;
    private readonly Func<string, WatchStoreHealth> _health;
    private readonly Func<string, DaemonHeartbeat?> _heartbeat;
    private readonly Func<string, PassPhaseStamp?> _pass;
    private readonly Func<DateTime> _utcNow;

    public WatchdSupervisorSource(
        Func<IReadOnlyList<WorkspaceRegistryEntry>>? registry = null,
        SupervisionState? state = null,
        WatchdDaemonProbe? daemon = null,
        Func<string, WatchStoreHealth>? health = null,
        Func<string, DaemonHeartbeat?>? heartbeat = null,
        Func<string, PassPhaseStamp?>? pass = null,
        Func<DateTime>? utcNow = null)
    {
        _registry = registry ?? WorkspaceRegistry.Load;
        _state = state ?? new SupervisionState();
        _daemon = daemon ?? new WatchdDaemonProbe();
        _health = health ?? (storeDir => WatchStoreHealth.Probe(storeDir, DateTime.UtcNow));
        _heartbeat = heartbeat ?? (storeDir => DaemonHeartbeatReader.TryRead(storeDir));
        _pass = pass ?? PassPhaseStampFile.TryRead;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public WatchdSupervisorView Read()
    {
        var daemon = _daemon.Inspect();
        var sessions = _registry()
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.StoreKey, StringComparer.Ordinal)
            .Select(Describe)
            .ToList();
        return new WatchdSupervisorView(daemon, sessions);
    }

    public bool IsWatcherRunning(string storeKey) => _health(StoreDirOf(storeKey)).IndexerRunning;

    private WatchdSessionRow Describe(WorkspaceRegistryEntry entry)
    {
        var desire = _state.DesireOf(entry.StoreKey);
        if (!WorkspaceStorePaths.IsSingleSegmentKey(entry.StoreKey) || !Directory.Exists(entry.RootPath))
        {
            return new WatchdSessionRow(
                entry.StoreKey,
                entry.Name,
                entry.RootPath,
                desire,
                WatchdSessionState.Stopped,
                "unresolved",
                "store row does not resolve on this machine",
                null);
        }

        var storeDir = StoreDirOf(entry.StoreKey);
        var health = _health(storeDir);
        var now = _utcNow();
        var pass = _pass(storeDir);
        var liveness = WatchLiveness.Classify(_heartbeat(storeDir), pass, now);
        return new WatchdSessionRow(
            entry.StoreKey,
            entry.Name,
            entry.RootPath,
            desire,
            ClassifyState(desire, health),
            liveness.State.ToString().ToLowerInvariant(),
            DescribePass(pass, now),
            health.OwnerPid);
    }

    private static WatchdSessionState ClassifyState(WatchDesire desire, WatchStoreHealth health) =>
        desire == WatchDesire.Stopped
            ? WatchdSessionState.Stopped
            : health.State switch
            {
                WatchIndexerState.Alive => WatchdSessionState.Running,
                WatchIndexerState.Foreign => WatchdSessionState.ManagedElsewhere,
                WatchIndexerState.Wedged => WatchdSessionState.Wedged,
                _ => WatchdSessionState.Stopped,
            };

    private string DescribePass(PassPhaseStamp? pass, DateTime now)
    {
        if (pass is null)
        {
            return "no pass recorded";
        }

        return $"{pass.Phase}, batch {pass.BatchSize}, progress {FormatAge(now - pass.LastProgressAt)} ago";
    }

    private static string FormatAge(TimeSpan age) => age.TotalMinutes < 1
        ? "under a minute"
        : age.TotalHours < 1
            ? $"{(int)age.TotalMinutes}m"
            : age.TotalHours < 24
                ? $"{(int)age.TotalHours}h {age.Minutes}m"
                : $"{(int)age.TotalDays}d";

    private static string StoreDirOf(string storeKey) => Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
}
