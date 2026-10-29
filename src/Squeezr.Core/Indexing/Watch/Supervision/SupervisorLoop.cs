using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class SupervisorLoop
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IWatchDaemonLauncher _launcher;
    private readonly SupervisionState _state;
    private readonly CrashLoopGuard _guard;
    private readonly Func<IReadOnlyList<WorkspaceRegistryEntry>> _registry;
    private readonly Func<string, WatchStoreHealth> _health;
    private readonly Func<string, DateTime> _binaryModifiedUtc;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<string, IWatchDaemonHandle> _managed = [];

    public SupervisorLoop(IWatchDaemonLauncher launcher)
        : this(launcher, new SupervisionState(), new CrashLoopGuard())
    {
    }

    public SupervisorLoop(IWatchDaemonLauncher launcher, SupervisionState state, CrashLoopGuard guard)
        : this(
            launcher,
            state,
            guard,
            WorkspaceRegistry.Load,
            storeDir => WatchStoreHealth.Probe(storeDir, DateTime.UtcNow),
            static path => File.GetLastWriteTimeUtc(path),
            static () => DateTime.UtcNow,
            PollInterval)
    {
    }

    internal SupervisorLoop(
        IWatchDaemonLauncher launcher,
        SupervisionState state,
        CrashLoopGuard guard,
        Func<IReadOnlyList<WorkspaceRegistryEntry>> registry,
        Func<string, WatchStoreHealth> health,
        Func<string, DateTime> binaryModifiedUtc,
        Func<DateTime> utcNow,
        TimeSpan pollInterval)
    {
        _launcher = launcher;
        _state = state;
        _guard = guard;
        _registry = registry;
        _health = health;
        _binaryModifiedUtc = binaryModifiedUtc;
        _utcNow = utcNow;
        _pollInterval = pollInterval;
    }

    public void Run(CancellationToken cancellationToken, Action<SupervisorSweep>? onSweep = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            onSweep?.Invoke(RunOnce());
            cancellationToken.WaitHandle.WaitOne(_pollInterval);
        }
    }

    public SupervisorSweep RunOnce()
    {
        var now = _utcNow();
        PruneExited();
        IReadOnlyList<WorkspaceRegistryEntry> entries;
        try
        {
            entries = _registry();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SupervisorSweep(now, [], ex.Message);
        }

        return new SupervisorSweep(now, entries.Select(entry => Supervise(entry, now)).ToList());
    }

    private StoreSupervision Supervise(WorkspaceRegistryEntry entry, DateTime now)
    {
        try
        {
            return SuperviseStore(entry, now);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Report(
                entry,
                _state.DesireOf(entry.StoreKey),
                null,
                WatchIndexerState.None,
                SupervisionAction.ProbeFailed,
                ex.Message);
        }
    }

    private StoreSupervision SuperviseStore(WorkspaceRegistryEntry entry, DateTime now)
    {
        var desire = _state.DesireOf(entry.StoreKey);
        if (!WorkspaceStorePaths.IsSingleSegmentKey(entry.StoreKey) || !Directory.Exists(entry.RootPath))
        {
            return Report(entry, desire, null, WatchIndexerState.None, SupervisionAction.SkippedRoot,
                "the registry row does not resolve to a workspace on this machine");
        }

        if (desire == WatchDesire.Stopped)
        {
            if (_managed.Remove(entry.StoreKey, out var held))
            {
                held.Stop();
                held.Dispose();
                return Report(entry, desire, null, WatchIndexerState.None, SupervisionAction.StoppedByDesire, null);
            }

            return Report(entry, desire, null, WatchIndexerState.None, SupervisionAction.LeftStopped, null);
        }

        if (_guard.IsWedged(entry.StoreKey))
        {
            return Report(entry, desire, null, WatchIndexerState.None, SupervisionAction.BlockedByCrashLoop,
                WedgedDetail());
        }

        if (_managed.TryGetValue(entry.StoreKey, out var managed))
        {
            if (!managed.HasExited)
            {
                if (IsDrifted(managed))
                {
                    managed.Stop();
                    managed.Dispose();
                    _managed.Remove(entry.StoreKey);
                    return Launch(entry, now, WatchIndexerState.None, SupervisionAction.DriftRestarted,
                        "the daemon binary changed since the process started");
                }

                return Report(entry, desire, null, WatchIndexerState.Alive, SupervisionAction.LeftRunning,
                    null, managed.Id);
            }

            _managed.Remove(entry.StoreKey);
            managed.Dispose();
        }

        var health = _health(Path.Combine(SqueezrHome.WorkspacesDir, entry.StoreKey));
        if (health.IndexerRunning)
        {
            return Report(entry, desire, health.OwnerPid, health.State, SupervisionAction.SkippedLiveOwner,
                health.OwnerKind is { } kind
                    ? $"the watch loop belongs to a live {kind} process"
                    : "the watch loop belongs to a live process");
        }

        return Launch(entry, now, health.State, SupervisionAction.Started,
            $"no live indexer ({health.State.ToString().ToLowerInvariant()})");
    }

    private StoreSupervision Launch(
        WorkspaceRegistryEntry entry,
        DateTime now,
        WatchIndexerState indexerState,
        SupervisionAction action,
        string detail)
    {
        if (!_guard.RegisterRestart(entry.StoreKey, now))
        {
            return Report(entry, WatchDesire.Running, null, indexerState, SupervisionAction.BlockedByCrashLoop,
                WedgedDetail());
        }

        try
        {
            var handle = _launcher.StartWatch(entry.RootPath);
            _managed[entry.StoreKey] = handle;
            return Report(entry, WatchDesire.Running, null, indexerState, action, detail, handle.Id);
        }
        catch (Exception ex)
        {
            // One store's spawn failure must not take the whole supervisor down; repeated failures
            // end in the crash-loop wedge above.
            return Report(entry, WatchDesire.Running, null, indexerState, SupervisionAction.StartFailed, ex.Message);
        }
    }

    private bool IsDrifted(IWatchDaemonHandle handle) =>
        handle.StartedUtc is { } started
        && handle.BinaryPath is { } binary
        && VersionDriftDetector.IsDrifted(started, _binaryModifiedUtc(binary));

    private void PruneExited()
    {
        foreach (var storeKey in _managed.Where(pair => pair.Value.HasExited).Select(pair => pair.Key).ToList())
        {
            if (_managed.Remove(storeKey, out var handle))
            {
                handle.Dispose();
            }
        }
    }

    private StoreSupervision Report(
        WorkspaceRegistryEntry entry,
        WatchDesire desire,
        int? ownerPid,
        WatchIndexerState indexerState,
        SupervisionAction action,
        string? detail,
        int? managedPid = null) =>
        new(entry.StoreKey, entry.Name, entry.RootPath, desire, indexerState, ownerPid, managedPid, action, detail);

    private static string WedgedDetail() =>
        $"restarts stopped after {CrashLoopGuard.RestartLimit} starts inside {CrashLoopGuard.Window.TotalMinutes:F0} minutes";
}
