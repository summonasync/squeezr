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
    private readonly object _resolveLock = new();
    private IEmbeddingProvider? _embeddings;
    private IVectorStore? _vectorStore;
    private bool _resolved;

    public CodeSearchTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache = null)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
    }

    [Description("Hybrid exact/semantic search over the indexed workspace: matches exact identifiers and paraphrased or domain-worded queries alike (\"VectorDimensionMismatchException\" or \"validate config file\"). Returns file:line matches with snippets, never full bodies — expand_span reveals a body around a hit. Results are refreshed on read: modified files are reindexed inline or reported in staleFiles.")]
    [McpServerTool(Name = "code_search", Title = "Search code")]
    public Task<object> SearchAsync(
        string query,
        string? repo = null,
        int? maxTokens = null,
        int? topK = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(
            ct => SearchCore(query, repo, maxTokens, topK, ct),
            cancellationToken);

    private async Task<object> SearchCore(
        string query,
        string? repo,
        int? maxTokens,
        int? topK,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var parameters = Params(query, repo, maxTokens, topK);
            if (_cache is not null && _cache.TryGet("code_search", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "code_search", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<SearchHitData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge);
                return hit;
            }

            var warnings = new List<string>();
            ResolveRuntime(root, warnings);

            var engine = new SearchEngine(root);
            var searchQuery = new SearchQuery(query, false, null, repo, null, null, topK, maxTokens ?? DefaultMaxTokens);
            var response = await engine.SearchHybridAsync(searchQuery, _embeddings, _vectorStore, cancellationToken);

            warnings.AddRange(response.Warnings);

            var freshness = Guard(root, response);
            if (freshness.Refreshed.Count > 0)
            {
                response = await engine.SearchHybridAsync(searchQuery, _embeddings, _vectorStore, cancellationToken);
                foreach (var warning in response.Warnings)
                {
                    if (!warnings.Contains(warning))
                    {
                        warnings.Add(warning);
                    }
                }
            }

            var data = response.Matches.Select(m => new SearchHitData(
                m.Repo, m.Path, m.Line, m.Score, m.Source, m.Snippet)).ToList();

            var envelope = ToolEnvelope.Wrap(data, response.TokensUsed, response.Truncated, warnings, root, freshness.Stale);
            QueryTelemetry.Record(root, _workspace.SessionId, "code_search", parameters,
                response.TokensUsed, response.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge);
            _cache?.Put("code_search", parameters, envelope);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, SearchResponse response) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            response.Matches.Take(GuardCandidateLimit).Select(m => (m.Repo, m.Path)).Distinct().ToList(),
            StaleLockWarningSink.ToStderr);

    private static object Params(string query, string? repo, int? maxTokens, int? topK) =>
        new { query, repo, maxTokens, topK };

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
        [property: JsonPropertyName("snippet")] string Snippet);
}
