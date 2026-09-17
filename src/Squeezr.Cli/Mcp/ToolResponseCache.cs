using System.Text.Json;
using System.Text.Json.Nodes;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

public sealed class ToolResponseCache(string workspaceRoot, int capacity = 100)
{
    private readonly int _capacity = capacity >= 1
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CachedEntry>> _index = new();
    private readonly LinkedList<CachedEntry> _recent = new();

    public long Generation
    {
        get
        {
            var metaDbPath = SqueezrPaths.MetaDbPath(workspaceRoot);
            return File.Exists(metaDbPath) ? File.GetLastWriteTimeUtc(metaDbPath).Ticks : 0;
        }
    }

    public bool TryGet(string tool, object parameters, out object? response)
    {
        var key = BuildKey(tool, parameters);
        lock (_sync)
        {
            if (!_index.TryGetValue(key, out var node))
            {
                response = null;
                return false;
            }

            _recent.Remove(node);
            _recent.AddFirst(node);
            response = node.Value.Response;
            return true;
        }
    }

    public void Put(string tool, object parameters, object response)
    {
        var key = BuildKey(tool, parameters);
        lock (_sync)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                existing.Value = existing.Value with { Response = response };
                return;
            }

            var node = _recent.AddFirst(new CachedEntry(key, response));
            _index[key] = node;
            while (_index.Count > _capacity)
            {
                var oldest = _recent.Last!;
                _recent.RemoveLast();
                _index.Remove(oldest.Value.Key);
            }
        }
    }

    private string BuildKey(string tool, object parameters) =>
        $"{tool}\n{Canonicalize(JsonSerializer.SerializeToNode(parameters))}\n{Generation}";

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

    private sealed record CachedEntry(string Key, object Response);
}
