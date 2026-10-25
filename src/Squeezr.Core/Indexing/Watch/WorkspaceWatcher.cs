using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Watch;

public interface IWatchStatus
{
    void PassStarted(int changedPaths);

    void PassCompleted(IndexResult result);

    void RetryScheduled(TimeSpan delay);

    void Faulted(string error);
}

public sealed class WorkspaceWatcher
{
    private const int EventBufferBytes = 64 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PassJoinTimeout = TimeSpan.FromSeconds(5);

    // A pass holds the store index.lock for its whole duration, so an unbounded pass is an unbounded
    // store outage (2026-10-03: 95 minutes over one blocked file open). The budget is a watchdog, not a
    // work estimate: legit passes finish far inside it, a pass that crosses it is treated as wedged.
    // The abort it requests is cooperative, so a pass parked inside a syscall cannot hear it: for that
    // shape the lock comes back only when the process dies, and restarting the daemon is the supported
    // recovery ('squeezr fleet stop <store-key>', then start again) — never a hand-deleted index.lock.
    public static readonly TimeSpan DefaultPassBudget = TimeSpan.FromMinutes(10);

    // A repo whose honest pass costs more than the base budget must converge, not livelock: abort, requeue
    // and burn the whole budget again forever, locking the store in every cycle. Each consecutive abort
    // doubles the armed budget up to this ceiling, and a completed pass snaps it back to the base.
    private static readonly TimeSpan MaxArmedPassBudget = TimeSpan.FromMinutes(60);

    private readonly WorkspaceIndexer _indexer = new();
    private readonly IWatchStatus? _status;
    private readonly IIndexProgress? _progress;
    private readonly Action<string>? _onStaleTakeover;
    private readonly WatchOwnerClaim? _claim;
    private readonly Task<WatchEmbeddingRuntime?>? _embeddings;
    private readonly TimeSpan _passBudget;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = [];

    // The budget a pass is actually armed with: it doubles on every consecutive overrun so a legitimately
    // slow repo converges instead of livelocking, and snaps back to the configured floor on a completed pass.
    private readonly PassBudgetLadder _passBudgetLadder;

    private List<FileSystemWatcher> _watchers = [];
    private SemanticSessionPool? _sessions;
    private WatchEventFilter? _filter;
    private CancellationTokenSource? _stopping;
    private Task _runTask = Task.CompletedTask;
    private DaemonHeartbeatWriter? _heartbeat;
    private string _workspaceRoot = string.Empty;
    private TimeSpan _debounceWindow;
    private TimeSpan _backoffCap;
    private DateTime _lastEventAt = DateTime.MinValue;
    private DateTime _lastForcedEmptyBatchAt = DateTime.MinValue;
    private DateTime _lastClaimBeat = DateTime.MinValue;
    private WatchEmbeddingRuntime? _runtime;
    private bool _runtimeResolved;
    private int _embedBudget = WatchConfig.DefaultEmbedBudgetPerPass;
    private bool _forcePass;
    private bool _running;

    public WorkspaceWatcher(
        IWatchStatus? status = null,
        IIndexProgress? progress = null,
        Action<string>? onStaleTakeover = null,
        WatchOwnerClaim? claim = null,
        Task<WatchEmbeddingRuntime?>? embeddings = null,
        TimeSpan? passBudget = null)
    {
        _status = status;
        _progress = progress;
        _onStaleTakeover = onStaleTakeover;
        _claim = claim;
        _embeddings = embeddings;
        _passBudget = passBudget ?? DefaultPassBudget;
        _passBudgetLadder = new PassBudgetLadder(_passBudget, MaxArmedPassBudget);
    }

    public Task StartAsync(
        string workspaceRoot,
        CancellationToken cancellationToken,
        string daemonKind = DaemonHeartbeat.DefaultKind)
    {
        if (_running)
        {
            throw new InvalidOperationException("Watcher is already running.");
        }
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                RemedyText.WorkspaceNotInitialized,
                WorkspaceException.NotInitializedCode);
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        var config = LoadConfig(_workspaceRoot);
        _debounceWindow = TimeSpan.FromSeconds(config.Watch.DebounceSeconds);
        _backoffCap = TimeSpan.FromSeconds(config.Watch.BackoffMaxSeconds);
        _embedBudget = config.Watch.EmbedBudgetPerPass;
        var repoRoots = ResolveRepoRoots(_workspaceRoot);
        var rulesByRepo = BuildRulesets(repoRoots, config);
        _filter = new WatchEventFilter(_workspaceRoot, repoRoots, rulesByRepo);

        var storeDir = SqueezrPaths.StoreDir(_workspaceRoot);
        // a leftover stamp naming a writer that is gone means that pass never finished: erase the record and
        // open with a catch-up pass, so the paths it had already dequeued do not go silent until the next
        // edit. A stamp naming a live writer is that daemon's wedge evidence and stays untouched.
        var cutShort = PassPhaseStampFile.ClearIfWriterGone(storeDir);

        lock (_gate)
        {
            _pending.Clear();
            // a drain-equipped watcher opens with a catch-up pass so pending left by earlier sessions
            // starts moving without waiting for an edit; embed-free watchers keep their zero-pass idle
            // unless the record above proves the last pass never completed
            _forcePass = _embeddings is not null || cutShort;
            _lastEventAt = DateTime.MinValue;
            _lastForcedEmptyBatchAt = DateTime.MinValue;
        }

        _lastClaimBeat = DateTime.MinValue;
        _watchers = CreateWatchers();
        _sessions = new SemanticSessionPool();
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;
        _heartbeat = new DaemonHeartbeatWriter(
            storeDir,
            daemonKind,
            onWarning: _status is null ? null : message => _status.Faulted(message));
        _heartbeat.Start();
        _runTask = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping is null)
        {
            return;
        }

        stopping.Cancel();
        try
        {
            // a pass wedged inside a syscall never answers the abort, and joining it forever would hang
            // the whole shutdown behind the one thread that is already lost
            await _runTask.WaitAsync(PassJoinTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            _status?.Faulted("watch pass ignored the stop request; abandoning it and shutting down anyway");
        }
        finally
        {
            stopping.Dispose();
            Interlocked.Exchange(ref _heartbeat, null)?.Dispose();
            _claim?.Release();
        }
    }

    private static SqueezrConfig LoadConfig(string workspaceRoot)
    {
        try
        {
            return ConfigFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or WorkspaceException)
        {
            return SqueezrConfig.CreateDefault();
        }
    }

    private static IReadOnlyList<string> ResolveRepoRoots(string workspaceRoot)
    {
        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        return manifest.Repos
            .Select(repo => Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path)))
            .Where(Directory.Exists)
            .ToList();
    }

    private static IReadOnlyDictionary<string, IgnoreRuleset> BuildRulesets(
        IReadOnlyList<string> repoRoots, SqueezrConfig config)
    {
        var result = new Dictionary<string, IgnoreRuleset>(StringComparer.Ordinal);
        foreach (var root in repoRoots)
        {
            result[root] = IgnoreRuleset.For(root, config);
        }
        return result;
    }

    private List<FileSystemWatcher> CreateWatchers()
    {
        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var root in _filter!.RepoRoots)
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = EventBufferBytes,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += (_, e) => OnEvent(e.FullPath, pathMayBeGone: false);
                watcher.Changed += (_, e) => OnEvent(e.FullPath, pathMayBeGone: false);
                watcher.Deleted += (_, e) => OnEvent(e.FullPath, pathMayBeGone: true);
                watcher.Renamed += (_, e) =>
                {
                    OnEvent(e.OldFullPath, pathMayBeGone: true);
                    OnEvent(e.FullPath, pathMayBeGone: false);
                };
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
        }
        catch
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
            throw;
        }
        return watchers;
    }

    internal void OnEvent(string fullPath, bool pathMayBeGone)
    {
        if (WatchEventFilter.IsIgnoreFileChange(fullPath))
        {
            lock (_gate)
            {
                _forcePass = true;
                _lastEventAt = DateTime.UtcNow;
            }
            return;
        }

        if (!_filter!.ShouldTrack(fullPath, pathMayBeGone))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add(fullPath);
            _lastEventAt = DateTime.UtcNow;
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _status?.Faulted($"file system watcher dropped events: {e.GetException().Message}");
        lock (_gate)
        {
            _forcePass = true;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (!RefreshClaim())
                {
                    return;
                }

                var batch = CollectDueBatch();
                if (batch is null)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RunPassAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            Interlocked.Exchange(ref _sessions, null)?.Dispose();
        }
    }

    internal List<string>? CollectDueBatch()
    {
        lock (_gate)
        {
            if (_pending.Count == 0 && !_forcePass)
            {
                return null;
            }

            if (!_forcePass && DateTime.UtcNow - _lastEventAt < _debounceWindow)
            {
                return null;
            }

            // a dropped-event error carries no paths of its own, and a storm keeps re-raising it: without a
            // settle window every error would buy the daemon a whole reconciliation pass back to back
            if (_pending.Count == 0 && DateTime.UtcNow - _lastForcedEmptyBatchAt < _debounceWindow)
            {
                return null;
            }

            var batch = _pending.ToList();
            _pending.Clear();
            _forcePass = false;
            if (batch.Count == 0)
            {
                _lastForcedEmptyBatchAt = DateTime.UtcNow;
            }

            return batch;
        }
    }

    // RefreshBeat answering anything but Acquired means the store changed hands while we ran (T1 contract),
    // so this is a hard stop — retrying a lost claim is how a second indexer gets born. The beat rides the
    // existing poll loop at the heartbeat interval; claim-less watchers pay nothing here.
    private bool RefreshClaim()
    {
        if (_claim is null)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        if (now - _lastClaimBeat < DaemonHeartbeat.BeatInterval)
        {
            return true;
        }

        var outcome = _claim.RefreshBeat();
        if (!outcome.IsOwner)
        {
            // demotion is where the mirror stops lying: the per-kind heartbeat is the pre-roster reader's only
            // answer to who runs the watch loop, and the claim has just moved on — so stamping this writer's
            // name into that file ends here, not at StopAsync. The per-pid beat keeps beating: this process is
            // still a live presence on the store, just not the indexer anymore.
            _heartbeat?.StopPublishingKindHeartbeat();
            _status?.Faulted("watch ownership lost: stopping watch passes");
            return false;
        }

        _lastClaimBeat = now;
        return true;
    }

    private async Task RunPassAsync(List<string> batch, CancellationToken cancellationToken)
    {
        _status?.PassStarted(batch.Count);
        var runtime = await ResolveRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
        {
            _heartbeat?.SetEmbeddingExecutor(runtime.Executor, runtime.ProviderName);
        }

        var storeDir = SqueezrPaths.StoreDir(_workspaceRoot);
        var passStart = DateTime.UtcNow;
        PassPhaseStampFile.Write(storeDir, new PassPhaseStamp(
            Environment.ProcessId, PassPhaseStamp.RunningPhase, passStart, passStart, batch.Count));
        // The budget rides the stop token: a shutdown must reach the pass so the store lock unwinds with
        // it instead of being stranded by the process exit (the leftover lock then needs a stale-takeover
        // every start). A pass parked inside a syscall still cannot hear either cancellation, which is why
        // the stamp left behind by a dead writer is the second, durable catch-up signal in StartAsync.
        var armed = _passBudgetLadder.Armed;
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (armed <= TimeSpan.Zero)
        {
            budget.Cancel();
        }
        else
        {
            budget.CancelAfter(armed);
        }
        var overrunSeen = 0;
        var overrun = budget.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested
                || Interlocked.Exchange(ref overrunSeen, 1) != 0)
            {
                return;
            }

            // the stamp is the only wedge signal that survives the wedge itself: a pass blocked inside a
            // syscall never reaches the abort below, so the watchdog writes "stuck" while it can still write
            PassPhaseStampFile.Write(storeDir, new PassPhaseStamp(
                Environment.ProcessId, PassPhaseStamp.StuckPhase, passStart, passStart, batch.Count));
            _status?.Faulted(
                $"watch pass exceeded its {FormatBudget(armed)} budget and is treated as wedged; the abort " +
                "requested here is cooperative, so a pass parked inside a syscall never hears it and keeps " +
                "the store lock with it: if the lock does not come back on its own, the supported recovery " +
                "is a daemon restart ('squeezr fleet stop <store-key>' and start again) — never a " +
                "hand-deleted index.lock");
        });
        var abandonedToStop = false;
        try
        {
            await RunPassCoreAsync(batch, runtime, budget.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the shutdown reached this pass through the linked token and its already-dequeued batch dies
            // with the process, so the record is rewritten as an explicit hand-off instead of being erased
            // (or left naming a phase that only a live writer's wedge owns). The next start consumes it.
            RequeueBatch(batch);
            PassPhaseStampFile.Write(storeDir, new PassPhaseStamp(
                Environment.ProcessId, PassPhaseStamp.InterruptedPhase, passStart, DateTime.UtcNow, batch.Count));
            abandonedToStop = true;
            throw;
        }
        finally
        {
            overrun.Dispose();
            budget.Dispose();
            if (!abandonedToStop)
            {
                PassPhaseStampFile.Clear(storeDir);
            }
        }
    }

    // A non-positive budget is the immediate-abort seam the tests use ("cancel at the first boundary") and
    // stays exactly that through the ladder: escalation can never un-arm the seam.
    internal TimeSpan ArmedPassBudget => _passBudgetLadder.Armed;

    private static string FormatBudget(TimeSpan budget) =>
        budget >= TimeSpan.FromSeconds(1)
            ? $"{budget.TotalSeconds:n0}s"
            : $"{budget.TotalMilliseconds:n0}ms";

    private async Task RunPassCoreAsync(
        List<string> batch,
        WatchEmbeddingRuntime? runtime,
        CancellationToken passToken,
        CancellationToken cancellationToken)
    {
        var backoff = InitialBackoff;
        while (true)
        {
            try
            {
                var heartbeat = _heartbeat;
                var progress = heartbeat is null
                    ? _progress
                    : new WatchActivityProgress(heartbeat, _progress);
                var result = _indexer.Index(
                    _workspaceRoot, progress: progress, onStaleTakeover: _onStaleTakeover,
                    changedPaths: batch, sessions: _sessions,
                    embeddings: runtime?.Provider, vectorStore: runtime?.Store,
                    embeddingProviderResolution: runtime?.Resolution,
                    embedBudgetPerPass: runtime is null ? null : _embedBudget,
                    cancellationToken: passToken);
                _passBudgetLadder.NoteCompleted();
                _status?.PassCompleted(result);
                _heartbeat?.Beat();
                if (runtime is not null && result.Embedded >= _embedBudget)
                {
                    // the budget saturated, so the queue provably still has work: re-arm an empty batch
                    // and let the debounce window pace the catch-up instead of waiting for an edit
                    lock (_gate)
                    {
                        _forcePass = true;
                    }
                }
                else if (runtime is not null && !result.EmbeddingsCompleted && result.Embedded > 0)
                {
                    // the phase died mid-drain after real progress: the un-attempted tail would sit forever
                    // behind a wait for edits. One re-arm, debounced as usual; the retry starts at the tail
                    // and a provider still failing there embeds 0, which re-arms nothing — a half-dead
                    // provider cannot spin this loop
                    lock (_gate)
                    {
                        _forcePass = true;
                    }
                }

                return;
            }
            catch (OperationCanceledException) when (passToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // the pass honestly overran its budget: double what the next one is armed with, so a slow
                // repo converges instead of burning the same window over and over with the store locked
                _passBudgetLadder.NoteOverrun();
                RequeueBatch(batch);
                _status?.Faulted("watch pass aborted at the pass budget boundary; the store lock went with it and the batch is re-queued");
                return;
            }
            catch (WorkspaceLockedException)
            {
                _heartbeat?.ClearActivity();
                _status?.RetryScheduled(backoff);
            }
            catch (VectorDimensionMismatchException ex)
            {
                // collection and model can never agree by retrying: the drain is dead for this serve, the
                // watcher is not. Passes keep indexing embed-free, and only a serve restart re-reads the config
                _status?.Faulted($"watch embed drain disabled: {ex.Message}");
                _runtime = null;
                _runtimeResolved = true;
                // the batch never got indexed (the mismatch throws before repo work): one embed-free
                // pass picks it up instead of holding the content hostage to the next edit
                RequeueBatch(batch, forcePass: true);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException
                and not StackOverflowException
                and not OperationCanceledException)
            {
                _status?.Faulted($"index pass failed: {ex.Message}");
                RequeueBatch(batch);
                return;
            }

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _backoffCap.Ticks));
        }
    }

    private void RequeueBatch(List<string> batch, bool forcePass = false)
    {
        lock (_gate)
        {
            foreach (var path in batch)
            {
                _pending.Add(path);
            }
            _lastEventAt = DateTime.UtcNow;
            _forcePass |= forcePass;
        }
    }

    private async Task<WatchEmbeddingRuntime?> ResolveRuntimeAsync(CancellationToken cancellationToken)
    {
        if (_embeddings is null)
        {
            return null;
        }

        if (_runtimeResolved)
        {
            return _runtime;
        }

        if (!_embeddings.IsCompleted)
        {
            await _embeddings.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_embeddings.Status == TaskStatus.RanToCompletion)
        {
            _runtime = _embeddings.Result;
        }
        else
        {
            _status?.Faulted(
                $"watch embed drain disabled: {_embeddings.Exception?.GetBaseException().Message}");
        }

        _runtimeResolved = true;
        return _runtime;
    }
}
