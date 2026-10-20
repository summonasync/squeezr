using System.Text.Json;
using System.Text.Json.Nodes;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

// Callers must serialize the TryGet→Put pair per key (McpToolGate runs one tool call at a time). The
// read-generation marker is what makes a stored answer describe the index and the config it was computed
// from; two overlapping calls on one key would let the second caller's stale answer license the first
// caller's stale answer.
public sealed class ToolResponseCache(string workspaceRoot, int capacity = 100)
{
    public const string InvalidationWarning = "cache: invalidated (index changed)";

    private readonly int _capacity = capacity >= 1
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CachedEntry>> _index = new();
    private readonly LinkedList<CachedEntry> _recent = new();
    private readonly HashSet<string> _invalidatedKeys = new();
    private readonly Dictionary<string, (long Index, long Policy)> _readGenerations = new();
    private (long Index, long Policy) _observedGeneration;
    private (long Index, long Policy) _announcedGeneration = (long.MinValue, long.MinValue);
    private bool _noticePending;

    // Two clocks carried as a pair, never blended into one number. meta.db moves when the index changes;
    // config.json moves when a policy that shapes the answer changes (indexing.rankExclude,
    // indexing.secretGuardExtra). An entry stays alive only while both of its components still match, so a
    // config whose mtime sits in the future (stepped clock, `cp -p` from a backup, a fixture pinning
    // stamps) cannot swallow index passes the way a max() blend did — the blend froze on the config stamp
    // and no eviction happened until the config itself was touched again. Carrying the patterns in the key
    // answers "which pattern set produced this answer"; the policy half answers "the config moved at all",
    // which is what stops a mask narrowed back from reconstituting its older key and resurrecting the
    // envelope that was cached before the widening.
    public (long Index, long Policy) Generation
    {
        get
        {
            var metaDbPath = SqueezrPaths.MetaDbPath(workspaceRoot);
            var index = File.Exists(metaDbPath) ? File.GetLastWriteTimeUtc(metaDbPath).Ticks : 0;
            var configPath = SqueezrPaths.ConfigPath(workspaceRoot);
            var policy = File.Exists(configPath) ? File.GetLastWriteTimeUtc(configPath).Ticks : 0;
            return (index, policy);
        }
    }

    public int Count
    {
        get
        {
            var generation = Generation;
            lock (_sync)
            {
                Retire(generation);
                return _index.Count;
            }
        }
    }

    public bool TryGet(string tool, object parameters, out object? response)
    {
        var key = BuildKey(tool, parameters);
        var generation = Generation;
        lock (_sync)
        {
            Retire(generation);
            if (_index.TryGetValue(key, out var node))
            {
                // Retire only sweeps when the generation moved, so an entry stored against an older one by a
                // call that raced an index write is caught here instead: it describes data that no longer holds.
                if (node.Value.Generation == generation)
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    _readGenerations.Remove(key);
                    response = node.Value.Response;
                    return true;
                }

                _recent.Remove(node);
                _index.Remove(key);
                _invalidatedKeys.Add(key);
            }

            // TryAdd, not indexer assignment: a second miss on the same key while the first is still in
            // flight must not vouch for the first caller's answer.
            _readGenerations.TryAdd(key, generation);
            if (_readGenerations.Count > _capacity * 4)
            {
                // A marker lives between the miss and its Put; a tool call that throws leaves one behind.
                // Wiping them costs a caching opportunity and, given the serialized caller contract above,
                // can only ever drop markers of calls that already died.
                _readGenerations.Clear();
                _readGenerations[key] = generation;
            }

            if (_invalidatedKeys.Remove(key) && _announcedGeneration != generation)
            {
                _announcedGeneration = generation;
                _noticePending = true;
            }

            response = null;
            return false;
        }
    }

    public string? TakeInvalidationWarning()
    {
        lock (_sync)
        {
            if (!_noticePending)
            {
                return null;
            }

            _noticePending = false;
            return InvalidationWarning;
        }
    }

    public void Put(string tool, object parameters, object response)
    {
        var key = BuildKey(tool, parameters);
        var generation = Generation;
        lock (_sync)
        {
            Retire(generation);
            // The response describes the generation the miss was read at, not whichever one is current when
            // the caller finally stores it. An index write landing mid-call would otherwise file a pre-write
            // answer under the post-write generation, and that entry survives the next invalidation.
            var described = generation;
            if (_readGenerations.Remove(key, out var readAt))
            {
                if (readAt.Index > generation.Index || readAt.Policy > generation.Policy)
                {
                    // Either clock went backwards (NTP step, a config restored from an older backup, or a
                    // test pinning an mtime into the past), so which index state this answer describes is
                    // unknowable. Refusing to cache beats a wrong tag.
                    return;
                }

                described = readAt;
            }

            if (_index.TryGetValue(key, out var existing))
            {
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                existing.Value = existing.Value with { Response = response, Generation = described };
                return;
            }

            var node = _recent.AddFirst(new CachedEntry(key, response, described));
            _index[key] = node;
            while (_index.Count > _capacity)
            {
                var oldest = _recent.Last!;
                _recent.RemoveLast();
                _index.Remove(oldest.Value.Key);
            }
        }
    }

    private void Retire((long Index, long Policy) generation)
    {
        if (generation == _observedGeneration)
        {
            return;
        }

        _observedGeneration = generation;
        _invalidatedKeys.Clear();
        var node = _recent.First;
        while (node is not null)
        {
            var next = node.Next;
            var entry = node.Value;
            if (entry.Generation != generation)
            {
                _invalidatedKeys.Add(entry.Key);
                _recent.Remove(node);
                _index.Remove(entry.Key);
            }

            node = next;
        }
    }

    private static string BuildKey(string tool, object parameters) =>
        $"{tool}\n{Canonicalize(JsonSerializer.SerializeToNode(parameters))}";

    private static string? Canonicalize(JsonNode? node) =>
        node switch
        {
            JsonObject obj => "{" + string.Join(",", obj
                .Where(p => p.Value is not null)
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{JsonSerializer.Serialize(p.Key)}:{Canonicalize(p.Value)}")) + "}",
            JsonArray array => "[" + string.Join(",", array.Select(Canonicalize)) + "]",
            _ => node?.ToJsonString()
        };

    private sealed record CachedEntry(string Key, object Response, (long Index, long Policy) Generation);
}
