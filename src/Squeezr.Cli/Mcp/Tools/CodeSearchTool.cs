using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSearchTool
{
    private const int GuardCandidateLimit = 50;

    private readonly McpWorkspace _workspace;
    private readonly object _resolveLock = new();
    private IEmbeddingProvider? _embeddings;
    private IVectorStore? _vectorStore;
    private bool _resolved;

    public CodeSearchTool(McpWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Hybrid exact/semantic search over the indexed workspace. Returns file:line matches with snippets
    /// (no full bodies); use expand_span (when available) to reveal a body around a hit.
    /// Results are refreshed on read: modified files are reindexed inline or reported in staleFiles.
    /// </summary>
    [McpServerTool(Name = "code_search", Title = "Search code")]
    public async Task<object> SearchAsync(
        string query,
        string? repo = null,
        int? maxTokens = null,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();
            ResolveRuntime(root, warnings);

            var engine = new SearchEngine(root);
            var searchQuery = new SearchQuery(query, false, null, repo, null, null, topK, maxTokens);
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

            return ToolEnvelope.Wrap(data, response.TokensUsed, response.Truncated, warnings, root, freshness.Stale);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, SearchResponse response) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            response.Matches.Take(GuardCandidateLimit).Select(m => (m.Repo, m.Path)).ToList());

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
