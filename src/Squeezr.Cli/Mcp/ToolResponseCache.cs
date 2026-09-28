using System.Text.Json;
using System.Text.Json.Nodes;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

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
    private long _observedGeneration;
    private long _announcedGeneration = long.MinValue;
    private bool _noticePending;

    public long Generation
    {
        get
        {
            var metaDbPath = SqueezrPaths.MetaDbPath(workspaceRoot);
            return File.Exists(metaDbPath) ? File.GetLastWriteTimeUtc(metaDbPath).Ticks : 0;
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
                _recent.Remove(node);
                _recent.AddFirst(node);
                response = node.Value.Response;
                return true;
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
            if (_index.TryGetValue(key, out var existing))
            {
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                existing.Value = existing.Value with { Response = response, Generation = generation };
                return;
            }

            var node = _recent.AddFirst(new CachedEntry(key, response, generation));
            _index[key] = node;
            while (_index.Count > _capacity)
            {
                var oldest = _recent.Last!;
                _recent.RemoveLast();
                _index.Remove(oldest.Value.Key);
            }
        }
    }

    private void Retire(long generation)
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

    private sealed record CachedEntry(string Key, object Response, long Generation);
}
