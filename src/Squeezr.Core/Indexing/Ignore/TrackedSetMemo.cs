namespace Squeezr.Core.Indexing.Ignore;

public sealed class TrackedSetMemo
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _ttl;
    private readonly Func<string, Action<string>?, GitTrackedSet?> _load;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public TrackedSetMemo(TimeSpan? ttl = null, Func<string, Action<string>?, GitTrackedSet?>? load = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _load = load ?? GitTrackedSet.Load;
    }

    public static TrackedSetMemo Shared { get; } = new();

    public GitTrackedSet? For(string repoRoot, Action<string>? warn = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_entries.TryGetValue(repoRoot, out var entry) && now - entry.LoadedAt < _ttl)
            {
                return entry.Set;
            }

            var set = _load(repoRoot, warn);
            _entries[repoRoot] = new Entry(set, now);
            return set;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private readonly record struct Entry(GitTrackedSet? Set, DateTimeOffset LoadedAt);
}
