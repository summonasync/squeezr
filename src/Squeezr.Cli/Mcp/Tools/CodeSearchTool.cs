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

    [Description("Hybrid exact/semantic search over the indexed workspace: matches exact identifiers and paraphrased or domain-worded queries alike (\"VectorDimensionMismatchException\" or \"validate config file\"). Returns file:line matches with snippets, never full bodies — expand_span reveals a body around a hit. One hit per file: the best span wins and the file's other spans collapse into alsoMatched, so topK counts distinct files, not spans. Pass prefix (e.g. \"src/\") to scope both the exact and the semantic arm to one directory subtree. kind filters what a hit can be: \"all\" (default) matches everything, \"code\" keeps program sources, \"docs\" keeps prose and config (markdown, text, json, yaml, toml, xml); the filter runs on both arms. Points without a kind payload (orphan or freshly rewritten points awaiting the backfill) match only kind=all. Results are refreshed on read: modified files are reindexed inline or reported in staleFiles.")]
    [McpServerTool(Name = "code_search", Title = "Search code")]
    public Task<object> SearchAsync(
        string query,
        string? repo = null,
        string? prefix = null,
        int? maxTokens = null,
        int? topK = null,
        string? kind = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(
            ct => SearchCore(query, repo, prefix, maxTokens, topK, kind, ct),
            cancellationToken);

    private async Task<object> SearchCore(
        string query,
        string? repo,
        string? prefix,
        int? maxTokens,
        int? topK,
        string? kind,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            if (!DocKindDetector.TryNormalizeFilter(kind, out var kindFilter))
            {
                throw new WorkspaceException(
                    $"Unknown kind '{kind}' — allowed values: all, code, docs.",
                    WorkspaceException.InvalidQueryCode);
            }

            var root = _workspace.Root;
            var normalizedPrefix = PathPrefix.Normalize(prefix);
            var rankExclusion = RankExclusion.Load(root);
            var secretGuardExtra = SecretGuardReader.Load(root).ExtraPatterns;
            var parameters = Params(query, repo, normalizedPrefix, maxTokens, topK, kindFilter, rankExclusion, secretGuardExtra);
            if (_cache is not null && _cache.TryGet("code_search", parameters, out var cached))
            {
                var hit = ((ToolEnvelope)cached!).MarkReplayed();
                // code_search has no mapped state: a refusal here really does drop matches, so false is what the
                // answer says, not a hole in the recording
                QueryTelemetry.Record(root, _workspace.SessionId, "code_search", parameters,
                    hit.PaidTokens, hit.Truncated, ((List<SearchHitData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                    baselineTokens: hit.Cost?.BaselineTokens, mapped: false);
                return hit;
            }

            var warnings = new List<string>();
            ResolveRuntime(root, warnings);
            warnings.AddRange(rankExclusion.Warnings);
            var coverage = _embeddings is not null && _vectorStore is not null ? LoadCoverage(root) : null;

            var engine = new SearchEngine(root, rankExclusion);
            var searchQuery = new SearchQuery(query, false, null, repo, null, null, topK, maxTokens ?? DefaultMaxTokens, normalizedPrefix, kindFilter);
            var response = await engine.SearchHybridAsync(searchQuery, _embeddings, _vectorStore, cancellationToken, coverage);

            warnings.AddRange(response.Warnings);

            var candidates = GuardCandidates(response);
            var freshness = Guard(root, candidates, cancellationToken);
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
                    response.Hint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy,
                    response.LegUsage is { } usage
                        ? $"exact {usage.ExactTokens}, semantic {usage.SemanticTokens}"
                        : null));
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
            if (StoreWriterNotice.For(root) is { } writerNotice)
            {
                warnings.Add(writerNotice);
            }
            if (unknownBaselineFiles > 0)
            {
                warnings.Add(
                    $"{unknownBaselineFiles} hit file(s) not in the index — excluded from the whole-file baseline");
            }

            // the candidate cap bounds what the content check covered, not what the answer claims: when the
            // answer names more files than that, the answer says how many the check actually reached
            var namedCount = namedFiles.Distinct().Count();
            var coverageLine = AnswerFreshness.CoverageWarning(candidates.Count, namedCount);
            if (coverageLine is not null)
            {
                warnings.Add(coverageLine);
            }

            var budgetLine = AnswerFreshness.BudgetWarning(freshness.Unrefreshed.Count);
            if (budgetLine is not null)
            {
                warnings.Add(budgetLine);
            }

            var envelope = ToolEnvelope.Wrap(data, response.TokensUsed, response.Truncated, warnings, root,
                freshness.Stale, cost: cost, fileFreshness: fileFreshness,
                freshness: AnswerFreshness.ForContent(
                    checkedCandidates: candidates.Count,
                    namedFiles: namedCount,
                    staleTotal: freshness.Stale.Count,
                    listed: freshness.Stale.Count,
                    laggingFiles: AnswerFreshness.CountLaggingFiles(fileFreshness),
                    stalePaths: freshness.Stale,
                    payloadLines: [coverageLine, budgetLine],
                    unrefreshedFiles: freshness.Unrefreshed.Count));
            var invalidated = _cache?.TakeInvalidationWarning();
            QueryTelemetry.Record(root, _workspace.SessionId, "code_search", parameters,
                envelope.PaidTokens, envelope.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                cacheInvalidated: invalidated is not null,
                baselineTokens: envelope.Cost?.BaselineTokens, mapped: false);
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

    private static IReadOnlyList<(string Repo, string Path)> GuardCandidates(SearchResponse response) =>
        [.. response.Matches
            .Take(FreshnessGuard.CandidateLimit)
            .Select(m => (m.Repo, m.Path))
            .Distinct()];

    private static FreshnessResult Guard(
        string root,
        IReadOnlyList<(string Repo, string Path)> candidates,
        CancellationToken cancellationToken) =>
        FreshnessGuard.CheckAndRefresh(root, candidates, StaleLockWarningSink.ToStderr, cancellationToken);

    private static object Params(
        string query,
        string? repo,
        string? prefix,
        int? maxTokens,
        int? topK,
        string? kind,
        RankExclusion rankExclusion,
        IReadOnlyList<string> secretGuardExtra) =>
        new
        {
            query,
            repo,
            prefix,
            maxTokens,
            topK,
            kind,
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
