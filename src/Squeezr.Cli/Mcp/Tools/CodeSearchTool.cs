using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSearchTool
{
    private const int GuardCandidateLimit = 50;

    private const int DefaultMaxTokens = 2000;

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;
    private readonly object _resolveLock = new();
    private IEmbeddingProvider? _embeddings;
    private IVectorStore? _vectorStore;
    private bool _resolved;

    public CodeSearchTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
    }

    [Description("Hybrid exact/semantic search over the indexed workspace: matches exact identifiers and paraphrased or domain-worded queries alike (\"VectorDimensionMismatchException\" or \"validate config file\"). Returns file:line matches with snippets, never full bodies — expand_span reveals a body around a hit. One hit per file: the best span wins and the file's other spans collapse into alsoMatched, so topK counts distinct files, not spans. Pass prefix (e.g. \"src/\") to scope both the exact and the semantic arm to one directory subtree. Results are refreshed on read: modified files are reindexed inline or reported in staleFiles.")]
    [McpServerTool(Name = "code_search", Title = "Search code")]
    public Task<object> SearchAsync(
        string query,
        string? repo = null,
        string? prefix = null,
        int? maxTokens = null,
        int? topK = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(
            ct => SearchCore(query, repo, prefix, maxTokens, topK, ct),
            cancellationToken);

    private async Task<object> SearchCore(
        string query,
        string? repo,
        string? prefix,
        int? maxTokens,
        int? topK,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var normalizedPrefix = PathPrefix.Normalize(prefix);
            var rankExclusion = RankExclusion.Load(root);
            var secretGuardExtra = SecretGuardReader.Load(root).ExtraPatterns;
            var parameters = Params(query, repo, normalizedPrefix, maxTokens, topK, rankExclusion, secretGuardExtra);
            if (_cache is not null && _cache.TryGet("code_search", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "code_search", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<SearchHitData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                    baselineTokens: hit.Cost?.BaselineTokens);
                return hit;
            }

            var warnings = new List<string>();
            ResolveRuntime(root, warnings);
            warnings.AddRange(rankExclusion.Warnings);
            var coverage = _embeddings is not null && _vectorStore is not null ? LoadCoverage(root) : null;

            var engine = new SearchEngine(root, rankExclusion);
            var searchQuery = new SearchQuery(query, false, null, repo, null, null, topK, maxTokens ?? DefaultMaxTokens, normalizedPrefix);
            var response = await engine.SearchHybridAsync(searchQuery, _embeddings, _vectorStore, cancellationToken, coverage);

            warnings.AddRange(response.Warnings);

            var freshness = Guard(root, response);
            if (freshness.Refreshed.Count > 0)
            {
                response = await engine.SearchHybridAsync(searchQuery, _embeddings, _vectorStore, cancellationToken, coverage);
                foreach (var warning in response.Warnings)
                {
                    if (!warnings.Contains(warning))
                    {
                        warnings.Add(warning);
                    }
                }
            }

            if (response.Truncated)
            {
                warnings.Add(HintText.TruncationLine(
                    response.TokensUsed,
                    maxTokens ?? DefaultMaxTokens,
                    response.Hint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy));
            }

            var data = response.Matches.Select(m => new SearchHitData(
                m.Repo, m.Path, m.Line, m.Score, m.Source, m.Snippet,
                m.AlsoMatched == 0 ? null : m.AlsoMatched)).ToList();

            var namedFiles = data.Select(d => (d.Repo, d.File)).ToList();
            var (cost, unknownBaselineFiles) = CostBaseline.Measure(
                root, namedFiles, response.TokensUsed, !response.Truncated);
            var fileFreshness = namedFiles.Count > 0
                ? FreshnessVerdict.Classify(root, namedFiles, guardStaleKeys: freshness.Stale)
                : null;
            if (unknownBaselineFiles > 0)
            {
                warnings.Add(
                    $"{unknownBaselineFiles} hit file(s) not in the index — excluded from the whole-file baseline");
            }

            var envelope = ToolEnvelope.Wrap(data, response.TokensUsed, response.Truncated, warnings, root,
                freshness.Stale, cost: cost, fileFreshness: fileFreshness);
            var invalidated = _cache?.TakeInvalidationWarning();
            QueryTelemetry.Record(root, _workspace.SessionId, "code_search", parameters,
                response.TokensUsed, response.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                cacheInvalidated: invalidated is not null,
                baselineTokens: envelope.Cost?.BaselineTokens);
            _cache?.Put("code_search", parameters, envelope);
            return envelope.WithWarning(invalidated);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static SemanticCoverage? LoadCoverage(string root)
    {
        using var meta = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(root));
        return meta?.GetSemanticCoverage();
    }

    private static FreshnessResult Guard(string root, SearchResponse response) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            response.Matches.Take(GuardCandidateLimit).Select(m => (m.Repo, m.Path)).Distinct().ToList(),
            StaleLockWarningSink.ToStderr);

    private static object Params(
        string query,
        string? repo,
        string? prefix,
        int? maxTokens,
        int? topK,
        RankExclusion rankExclusion,
        IReadOnlyList<string> secretGuardExtra) =>
        new
        {
            query,
            repo,
            prefix,
            maxTokens,
            topK,
            // cache generations track meta.db only, so without the patterns in the key a config
            // edit would keep being served from the pre-exclusion / pre-guard envelope
            rankExclude = rankExclusion.IsActive ? rankExclusion.Patterns : null,
            secretGuardExtra
        };

    private void ResolveRuntime(string root, List<string> warnings)
    {
        if (_resolved)
        {
            return;
        }

        lock (_resolveLock)
        {
            if (_resolved)
            {
                return;
            }

            warnings.AddRange(EmbeddingRuntimeFactory.TryCreate(root, withProvider: true, out var provider, out var store));
            _embeddings = provider;
            _vectorStore = store;
            _resolved = provider is not null;
        }
    }

    private sealed record SearchHitData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("snippet")] string Snippet,
        [property: JsonPropertyName("alsoMatched")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? AlsoMatched);
}
