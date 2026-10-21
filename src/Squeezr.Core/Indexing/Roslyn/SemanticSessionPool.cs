namespace Squeezr.Core.Indexing.Roslyn;

/// <summary>
/// One reused Roslyn session per repo root, kept for the lifetime of a watch serve.
/// Single-writer by contract, not by construction: WorkspaceIndexer.Index takes the store IndexLock
/// (CreateNew + FileShare.None, so a second pass throws instead of racing) and the watcher loop runs
/// passes back to back on one thread. Handing this pool to the read path (FreshnessGuard -> IndexFile)
/// means serialising Acquire/Discard/RemoveDocuments here first; Acquire only closes the double-create
/// leak today by disposing whichever session lost the write.
/// </summary>
public sealed class SemanticSessionPool : IDisposable
{
    private static readonly TimeSpan ReopenCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReopenMemory> _reopens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _deferred = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetryWindow> _retryWindows = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;

    public SemanticSessionPool(Func<DateTime>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public int CreatedSessions { get; private set; }

    public bool IsWarm(string repoRoot, bool semantic)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(repoRoot, out var entry)
                   && entry.Semantic == semantic
                   && !entry.Session.DisposeAttempted;
        }
    }

    /// <summary>
    /// Hands out the session this root is holding, creating one when the pool is cold. The outcome is
    /// the ownership contract: Pooled means the pool keeps the session and the caller must not dispose
    /// it, OneShot means the caller got a fallback session it owns. Throttled and CreateFailed hand out
    /// no session, and the caller must put the files it wanted to parse back into scope instead of
    /// trusting the committed sha. An entry whose session was disposed counts as cold: a misread of
    /// ownership anywhere upstream degrades to a recreate, never to serving a corpse.
    /// </summary>
    public AcquireOutcome Acquire(string repoRoot, bool semantic, out RoslynSession? session, Action<string> notice)
    {
        RoslynSession? candidate;
        lock (_gate)
        {
            candidate = null;
            if (_entries.TryGetValue(repoRoot, out var entry) && entry.Semantic == semantic)
            {
                if (entry.Session.DisposeAttempted)
                {
                    _entries.Remove(repoRoot);
                }
                else
                {
                    candidate = entry.Session;
                }
            }
        }

        if (candidate is not null)
        {
            session = candidate;
            return AcquireOutcome.Pooled;
        }

        // a root that cannot produce a cached session would otherwise pay a full solution open on every
        // debounce — the very storm shape this serve exists to remove, so the failed attempt is remembered
        var now = _utcNow();
        if (IsRetryThrottled(repoRoot, now, out var announce))
        {
            if (announce)
            {
                notice($"no reusable session for '{repoRoot}'; the next create attempt is throttled for one minute");
            }

            session = null;
            return AcquireOutcome.Throttled;
        }

        Discard(repoRoot);

        RoslynSession created;
        try
        {
            created = RoslynSession.Create(repoRoot, semantic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            ThrottleRetry(repoRoot);
            notice(ex.Message);
            session = null;
            return AcquireOutcome.CreateFailed;
        }

        CreatedSessions++;

        // an entry answers only for the mode it actually achieved, and a fallback session is never
        // cached at all: pooling one would hold the serve at fallback quality until the process dies
        if (created.Degraded || created.Semantic != semantic)
        {
            ThrottleRetry(repoRoot);
            session = created;
            return AcquireOutcome.OneShot;
        }

        RoslynSession kept;
        RoslynSession? loser;
        lock (_gate)
        {
            if (_entries.TryGetValue(repoRoot, out var existing)
                && existing.Semantic == created.Semantic
                && !existing.Session.DisposeAttempted)
            {
                kept = existing.Session;
                loser = created;
            }
            else
            {
                kept = created;
                loser = existing?.Session;
                _entries[repoRoot] = new Entry(created, created.Semantic);
            }

            _retryWindows.Remove(repoRoot);
        }

        loser?.Dispose();
        session = kept;
        return AcquireOutcome.Pooled;
    }

    /// <summary>
    /// Decides whether an unresolved-project rejection is worth a reload. Reopening the solution is the
    /// expensive recovery (a full pass on this repo costs minutes of CPU), so it fires once per new path
    /// and at most once per cooldown — an oscillating batch cannot keep buying full passes.
    /// </summary>
    public bool ShouldReopen(string repoRoot, IReadOnlyCollection<string> unresolvedPaths)
    {
        if (unresolvedPaths.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _utcNow();
            if (!_reopens.TryGetValue(repoRoot, out var memory))
            {
                _reopens[repoRoot] = new ReopenMemory(now, new HashSet<string>(unresolvedPaths, StringComparer.Ordinal));
                return true;
            }

            var unseen = unresolvedPaths.Where(path => !memory.Rejected.Contains(path)).ToList();
            if (unseen.Count == 0 || now - memory.ReopenedAt < ReopenCooldown)
            {
                return false;
            }

            memory.ReopenedAt = now;
            memory.Rejected.UnionWith(unseen);
            return true;
        }
    }

    public IReadOnlyCollection<string> DeferredSemantic(string repoRoot)
    {
        lock (_gate)
        {
            return _deferred.TryGetValue(repoRoot, out var paths) ? paths.ToArray() : [];
        }
    }

    public void ReplaceDeferredSemantic(string repoRoot, IReadOnlyCollection<string> relativePaths)
    {
        lock (_gate)
        {
            if (relativePaths.Count == 0)
            {
                _deferred.Remove(repoRoot);
                return;
            }

            _deferred[repoRoot] = new HashSet<string>(relativePaths, StringComparer.Ordinal);
        }
    }

    public void RemoveDocuments(string repoRoot, IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        RoslynSession? session;
        lock (_gate)
        {
            session = _entries.TryGetValue(repoRoot, out var entry) && !entry.Session.DisposeAttempted
                ? entry.Session
                : null;
            if (_deferred.TryGetValue(repoRoot, out var deferred))
            {
                foreach (var path in relativePaths)
                {
                    deferred.Remove(path);
                }

                if (deferred.Count == 0)
                {
                    _deferred.Remove(repoRoot);
                }
            }
        }

        if (session is null)
        {
            return;
        }

        foreach (var path in relativePaths)
        {
            session.RemoveDocument(path);
        }
    }

    public void PruneTo(IReadOnlyCollection<string> repoRoots)
    {
        var keep = new HashSet<string>(repoRoots, StringComparer.Ordinal);
        List<RoslynSession> dropped = [];
        lock (_gate)
        {
            foreach (var root in _entries.Keys.Where(root => !keep.Contains(root)).ToList())
            {
                dropped.Add(_entries[root].Session);
                _entries.Remove(root);
            }

            foreach (var root in _reopens.Keys.Where(root => !keep.Contains(root)).ToList())
            {
                _reopens.Remove(root);
            }

            foreach (var root in _deferred.Keys.Where(root => !keep.Contains(root)).ToList())
            {
                _deferred.Remove(root);
            }

            foreach (var root in _retryWindows.Keys.Where(root => !keep.Contains(root)).ToList())
            {
                _retryWindows.Remove(root);
            }
        }

        foreach (var session in dropped)
        {
            session.Dispose();
        }
    }

    public void Discard(string repoRoot)
    {
        RoslynSession? stale;
        lock (_gate)
        {
            if (!_entries.Remove(repoRoot, out var entry))
            {
                return;
            }

            stale = entry.Session;
        }

        stale.Dispose();
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
            _reopens.Clear();
            _deferred.Clear();
            _retryWindows.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Session.Dispose();
        }
    }

    private bool IsRetryThrottled(string repoRoot, DateTime now, out bool announce)
    {
        announce = false;
        lock (_gate)
        {
            if (!_retryWindows.TryGetValue(repoRoot, out var window))
            {
                return false;
            }

            if (now >= window.NotBefore)
            {
                _retryWindows.Remove(repoRoot);
                return false;
            }

            // the window announces itself once: every later pass inside it is the same fact and rides
            // the throttled counter instead of repeating the warning
            announce = !window.Announced;
            window.Announced = true;
            return true;
        }
    }

    private void ThrottleRetry(string repoRoot)
    {
        lock (_gate)
        {
            // armed from the moment the attempt finished, so a create that burned minutes keeps its
            // full cooldown instead of handing back the time it spent
            _retryWindows[repoRoot] = new RetryWindow(_utcNow() + RetryCooldown);
        }
    }

    private sealed record Entry(RoslynSession Session, bool Semantic);

    private sealed class RetryWindow(DateTime notBefore)
    {
        public DateTime NotBefore { get; } = notBefore;

        public bool Announced { get; set; }
    }

    private sealed class ReopenMemory(DateTime reopenedAt, HashSet<string> rejected)
    {
        public DateTime ReopenedAt { get; set; } = reopenedAt;

        public HashSet<string> Rejected { get; } = rejected;
    }
}
