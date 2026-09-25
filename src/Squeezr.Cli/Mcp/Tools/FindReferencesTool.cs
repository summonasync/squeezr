using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class FindReferencesTool
{
    private const int GuardCandidateLimit = 50;

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;

    public FindReferencesTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
    }

    [Description("Callers of a symbol before you edit it: every reference as file:line with the reference kind and the enclosing symbol, name-exact with no false positives (unlike grep on a name), captured at index time by Roslyn — exact, partial classes included; span-based inference is only a fallback for indexes built before enclosing capture. Returns a tool error if the symbol is not indexed. Pair with symbol_lookup to jump from a match to its usages.")]
    [McpServerTool(Name = "find_references", Title = "Find references")]
    public object Find(string symbol, string? repo = null) =>
        _gate.Run(() => FindCore(symbol, repo));

    private object FindCore(string symbol, string? repo)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var parameters = Params(symbol, repo);
            if (_cache is not null && _cache.TryGet("find_references", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "find_references", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<RefData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
                return hit;
            }

            var warnings = new List<string>();
            var lookup = new RefLookup(root);

            var result = lookup.Find(symbol, repo);

            var freshness = Guard(root, result);
            if (freshness.Refreshed.Count > 0)
            {
                result = lookup.Find(symbol, repo);
            }

            var data = BuildData(root, result);

            var tokensUsed = data.Sum(d => TokenCounter.Estimate($"{d.File}:{d.Line} {d.Kind}"));

            var envelope = ToolEnvelope.Wrap(data, tokensUsed, false, warnings, root, freshness.Stale);
            QueryTelemetry.Record(root, _workspace.SessionId, "find_references", parameters,
                tokensUsed, false, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
            _cache?.Put("find_references", parameters, envelope);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static List<RefData> BuildData(string root, RefLookupResult result)
    {
        var flattened = result.Groups
            .SelectMany(g => g.Refs.Select(r => (g.Repo, Ref: r)))
            .ToList();
        if (flattened.All(x => x.Ref.EnclosingSymbol is not null))
        {
            return flattened
                .Select(x => new RefData(x.Ref.File, x.Ref.Line, x.Ref.Kind, x.Ref.EnclosingSymbol))
                .ToList();
        }

        var symbolsByFile = new Dictionary<(string Repo, string File), IReadOnlyList<SymbolDocument>>();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        return flattened
            .Select(x => new RefData(
                x.Ref.File,
                x.Ref.Line,
                x.Ref.Kind,
                x.Ref.EnclosingSymbol ?? FindEnclosing(LoadSymbols(store, x.Repo, x.Ref.File, symbolsByFile), x.Ref.Line)))
            .ToList();
    }

    private static IReadOnlyList<SymbolDocument> LoadSymbols(
        MetaStore store,
        string repo,
        string file,
        Dictionary<(string Repo, string File), IReadOnlyList<SymbolDocument>> cache)
    {
        var key = (repo, file);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var repoDoc = store.FindRepo(repo);
        var fileDoc = repoDoc is null ? null : store.ListFiles(repoDoc.Id).FirstOrDefault(f => f.Path.Equals(file, StringComparison.Ordinal));
        var symbols = fileDoc is null
            ? []
            : store.ListSymbolsByFile(fileDoc.Id);
        cache[key] = symbols;
        return symbols;
    }

    private static string? FindEnclosing(IReadOnlyList<SymbolDocument> symbols, int line) =>
        symbols
            .Where(s => s.StartLine <= line && line <= s.EndLine)
            .OrderBy(s => s.EndLine - s.StartLine)
            .Select(s => s.ContainerName is null ? s.Name : $"{s.ContainerName}.{s.Name}")
            .FirstOrDefault();

    private static FreshnessResult Guard(string root, RefLookupResult result) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            result.Groups
                .SelectMany(g => g.Refs.Select(r => (g.Repo, r.File)).Append((g.Repo, g.File)))
                .Distinct()
                .Take(GuardCandidateLimit)
                .ToList(),
            StaleLockWarningSink.ToStderr);

    private static object Params(string symbol, string? repo) =>
        new { symbol, repo };

    private sealed record RefData(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("enclosingSymbol")] string? EnclosingSymbol);
}
