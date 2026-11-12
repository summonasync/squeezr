using Squeezr.Cli.Daemons;
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
        var supervision = _state.Read();
        var sessions = _registry()
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.StoreKey, StringComparer.Ordinal)
            .Select(entry => Describe(entry, daemon.Running, supervision))
            .ToList();
        return new WatchdSupervisorView(
            daemon,
            sessions,
            supervision.Status == SupervisionStateStatus.Unreadable
                ? $"{supervision.FilePath} {supervision.Fault}"
                : null);
    }

    public bool IsWatcherRunning(string storeKey) => _health(StoreDirOf(storeKey)).IndexerRunning;

    private WatchdSessionRow Describe(
        WorkspaceRegistryEntry entry,
        bool supervisorAlive,
        SupervisionSnapshot supervision)
    {
        // The snapshot's own DesireOf answers the running default for an unreadable file. That is fine for the
        // state cell — the wish is not what the body is doing there — and is never fine for the wish column,
        // so the row carries the distinction and the renderer prints the unknown where the guess would be.
        var desireKnown = supervision.Status != SupervisionStateStatus.Unreadable;
        var desire = supervision.DesireOf(entry.StoreKey);
        if (!FleetStoreKeyResolver.Resolves(entry.StoreKey, entry.RootPath))
        {
            return new WatchdSessionRow(
                entry.StoreKey,
                entry.Name,
                entry.RootPath,
                desire,
                WatchdSessionState.Stopped,
                "unresolved",
                "store row does not resolve on this machine",
                null)
            { DesireKnown = desireKnown, RootResolves = false };
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
            ClassifyState(desire, supervisorAlive, health),
            liveness.State.ToString().ToLowerInvariant(),
            DescribePass(pass, now),
            health.OwnerPid)
        { DesireKnown = desireKnown };
    }

    // The same dead body answers three different words, and the pair is the fleet's: stopped is a wish the
    // operator wrote, starting is a wish the live supervisor is on its way to carry out, idle is a wish nobody
    // is left to carry out. Naming the middle one "stopped" here while the fleet column called it "starting"
    // made the two surfaces contradict each other about one store (113/R7, review-113-t4-01 obstacle 5).
    private static WatchdSessionState ClassifyState(
        WatchDesire desire,
        bool supervisorAlive,
        WatchStoreHealth health) =>
        desire == WatchDesire.Stopped
            ? WatchdSessionState.Stopped
            : health.State switch
            {
                WatchIndexerState.Alive => WatchdSessionState.Running,
                WatchIndexerState.Foreign => WatchdSessionState.ManagedElsewhere,
                WatchIndexerState.Wedged => WatchdSessionState.Wedged,
                _ => supervisorAlive ? WatchdSessionState.Starting : WatchdSessionState.NoSupervisor,
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
