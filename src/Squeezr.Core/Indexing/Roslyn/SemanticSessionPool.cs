namespace Squeezr.Core.Indexing.Roslyn;

public sealed class SemanticSessionPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int CreatedSessions { get; private set; }

    public bool IsWarm(string repoRoot, bool semantic)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(repoRoot, out var entry) && entry.Semantic == semantic;
        }
    }

    public RoslynSession? Acquire(string repoRoot, bool semantic, Action<Exception> createFailed)
    {
        RoslynSession? candidate;
        lock (_gate)
        {
            candidate = _entries.TryGetValue(repoRoot, out var entry) && entry.Semantic == semantic ? entry.Session : null;
        }

        if (candidate is not null)
        {
            return candidate;
        }

        Discard(repoRoot);

        RoslynSession session;
        try
        {
            session = RoslynSession.Create(repoRoot, semantic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            createFailed(ex);
            return null;
        }

        lock (_gate)
        {
            CreatedSessions++;
            _entries[repoRoot] = new Entry(session, semantic);
        }

        return session;
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
            session = _entries.TryGetValue(repoRoot, out var entry) ? entry.Session : null;
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
        }

        foreach (var entry in entries)
        {
            entry.Session.Dispose();
        }
    }

    private sealed record Entry(RoslynSession Session, bool Semantic);
}
