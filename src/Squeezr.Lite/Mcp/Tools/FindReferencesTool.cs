using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Lite.Commands;
using Squeezr.Lite.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp.Tools;

[McpServerToolType]
public sealed class FindReferencesTool
{
    private const int LaggingNamesInWarning = 3;

    private readonly LiteWorkspace _workspace;
    private readonly LiteToolGate _gate;
    private readonly ClientIdentity _identity;

    public FindReferencesTool(LiteWorkspace workspace, LiteToolGate gate, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
    }

    [Description("Callers of a symbol before you edit it: every reference as file:line with the reference kind and the enclosing symbol, name-exact with no false positives (unlike grep on a name), one row per callsite even when several same-name definitions share the reference set. Every row names its repo, so callsites that only look identical across repositories stay distinct; the envelope also carries definitions ({repo, kind, file, startLine, endLine}) naming the declarations behind the rows: whenever two or more same-name declarations share one callsite list, or nothing calls the symbol at all. A row set belongs to the declaring file's last parse: a caller indexed after a declaring file's newest parse can leave this stale — the declaring files appear in warnings with the lag named, and 'sqzrlite index' reconciles every caller now. Returns a tool error if the symbol is not indexed. Pass the symbol as 'symbol' or its alias 'name' — both are accepted, a wrong-param error names both. Pair with symbol_lookup to jump from a match to its usages. The files this answer names are freshness-checked on read: modified files are reindexed inline within a per-call budget or reported in staleFiles, and the freshness block counts what was healed and what was refused.")]
    [McpServerTool(Name = "find_references", Title = "Find references")]
    public Task<object> FindAsync(
        string? symbol = null,
        string? repo = null,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(ct => Task.FromResult(FindCore(symbol, name, repo, ct)), cancellationToken);

    private object FindCore(string? symbol, string? name, string? repo, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var symbolName = SymbolParamAlias.Resolve("find_references", symbol, name, "symbol", "name");
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();
            var lookup = new RefLookup(root);
            var result = lookup.Find(symbolName, repo);

            var namedUniverse = AnsweredFiles(result);
            var candidates = QueryFreshness.Candidates(namedUniverse);
            var freshness = QueryFreshness.Guard(root, candidates, cancellationToken);
            if (freshness.Refreshed.Count > 0)
            {
                result = lookup.Find(symbolName, repo);
            }

            var data = BuildData(root, result);
            var definitions = BuildDefinitions(result, data.Count);

            var tokensUsed = data.Sum(d => TokenCounter.Estimate($"{d.File}:{d.Line} {d.Kind}"));

            if (result.GuardWarnings is { } withheld)
            {
                warnings.AddRange(withheld);
            }

            var lagging = result.LaggingDeclarations ?? [];
            string? laggingLine = lagging.Count > 0 ? LaggingWarning(lagging) : null;
            if (laggingLine is not null)
            {
                warnings.Add(laggingLine);
            }

            var carried = MergeStale(freshness.Stale, lagging);
            var namedFiles = data.Select(d => (d.Repo, d.File)).ToList();
            var (cost, unknownBaselineFiles) = CostBaseline.Measure(
                root, namedFiles, tokensUsed, true);
            if (WriterNotice.For(root) is { } writerNotice)
            {
                warnings.Add(writerNotice);
            }
            if (unknownBaselineFiles > 0)
            {
                warnings.Add(
                    $"{unknownBaselineFiles} referenced file(s) not in the index — excluded from the whole-file baseline");
            }

            var coverageLine = AnswerFreshness.CoverageWarning(candidates.Count, namedUniverse.Count);
            if (coverageLine is not null)
            {
                warnings.Add(coverageLine);
            }

            var budgetLine = AnswerFreshness.BudgetWarning(freshness.Unrefreshed.Count);
            if (budgetLine is not null)
            {
                warnings.Add(budgetLine);
            }

            var envelope = ToolEnvelope.Wrap(data, tokensUsed, false, warnings, root,
                carried, definitions: definitions, cost: cost, freshness: AnswerFreshness.ForContent(
                    candidates.Count, freshness.Refreshed.Count, carried.Count,
                    carried, [laggingLine, coverageLine, budgetLine], freshness.Unrefreshed.Count));
            QueryTelemetry.Record(root, _workspace.SessionId, "find_references",
                new { symbol = symbolName, repo },
                envelope.PaidTokens, false, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version, mapped: false);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static List<(string Repo, string File)> AnsweredFiles(RefLookupResult result) =>
    [
        .. result.Groups
            .SelectMany(g => g.Refs.Select(r => (g.Repo, r.File)))
            .Concat(result.Groups.SelectMany(g => g.Definitions.Select(d => (g.Repo, d.File))))
            .Distinct()
    ];

    private static IReadOnlyList<string> MergeStale(
        IReadOnlyList<string> guardStale,
        IReadOnlyList<GhostRefDeclaration> lagging) =>
        [.. guardStale
            .Concat(lagging.Select(d => $"{d.Repo}/{d.File}"))
            .Distinct()];

    private static List<RefData> BuildData(string root, RefLookupResult result)
    {
        var flattened = result.Groups
            .SelectMany(g => g.Refs.Select(r => (g.Repo, Ref: r)))
            .DistinctBy(x => (x.Repo, x.Ref.File, x.Ref.Line, x.Ref.Kind, x.Ref.EnclosingSymbol))
            .ToList();
        if (flattened.All(x => x.Ref.EnclosingSymbol is not null))
        {
            return flattened
                .Select(x => new RefData(x.Repo, x.Ref.File, x.Ref.Line, x.Ref.Kind, x.Ref.EnclosingSymbol))
                .ToList();
        }

        var symbolsByFile = new Dictionary<(string Repo, string File), IReadOnlyList<SymbolDocument>>();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        return flattened
            .Select(x => new RefData(
                x.Repo,
                x.Ref.File,
                x.Ref.Line,
                x.Ref.Kind,
                x.Ref.EnclosingSymbol ?? FindEnclosing(LoadSymbols(store, x.Repo, x.Ref.File, symbolsByFile), x.Ref.Line)))
            .ToList();
    }

    private static IReadOnlyList<RefDefinitionData>? BuildDefinitions(RefLookupResult result, int flatRowCount)
    {
        var declarations = result.Groups
            .SelectMany(g => g.Definitions, (g, d) => new RefDefinitionData(g.Repo, d.Kind, d.File, d.StartLine, d.EndLine))
            .ToList();
        if (flatRowCount > 0 && result.Groups.All(g => g.Definitions.Count == 1))
        {
            return null;
        }

        return declarations;
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

    private static string LaggingWarning(IReadOnlyList<GhostRefDeclaration> lagging)
    {
        var names = lagging.Take(LaggingNamesInWarning).Select(d => $"{d.Repo}/{d.File}");
        var rest = lagging.Count > LaggingNamesInWarning ? $", +{lagging.Count - LaggingNamesInWarning} more" : string.Empty;
        var rows = lagging.Sum(d => d.LaggingRows);
        return $"refs lag for {lagging.Count} declaring file(s) ({string.Join(", ", names)}{rest}), {rows} row(s) " +
               "written before a caller's newest index pass — a callsite removed in that caller can still show here " +
               $"and one added can be missing; '{LiteRemedy.IndexCommand}' verifies all now";
    }

    private sealed record RefData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("enclosingSymbol")] string? EnclosingSymbol);
}
