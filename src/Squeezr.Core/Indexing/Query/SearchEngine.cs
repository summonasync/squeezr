using Squeezr.Core.Embeddings;

namespace Squeezr.Core.Indexing.Query;

public sealed class SearchEngine
{
    private readonly string _workspaceRoot;

    public SearchEngine(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public SearchResponse Search(SearchQuery query)
    {
        var matches = ExactSearchPlan.Execute(_workspaceRoot, query, trackChunkKeys: false).Select(hit => hit.Match).ToList();
        return Finalize(matches, query, []);
    }

    public async Task<SearchResponse> SearchHybridAsync(
        SearchQuery query,
        IEmbeddingProvider? embeddings,
        IVectorStore? vectorStore,
        CancellationToken ct)
    {
        var exact = ExactSearchPlan.Execute(_workspaceRoot, query, trackChunkKeys: true);
        var semantic = new List<(string Key, SearchMatch Match)>();
        var warnings = new List<string>();

        if (vectorStore is not null && embeddings is not null)
        {
            try
            {
                if (!await vectorStore.IsAvailableAsync(ct))
                {
                    warnings.Add("vector store is unavailable, falling back to exact search only");
                }
                else if (await vectorStore.CountAsync(ct) > 0)
                {
                    semantic = await SemanticHitsResolver.Resolve(_workspaceRoot, query, embeddings, vectorStore, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"vector search failed ({ex.Message}), falling back to exact search only");
            }
        }

        var exactRanked = exact.Select(hit => (hit.Key!, hit.Match)).ToList();
        var merged = RrfMerger.Merge(exactRanked, semantic);
        return Finalize(merged, query, warnings);
    }

    private static SearchResponse Finalize(IReadOnlyList<SearchMatch> ranked, SearchQuery query, IReadOnlyList<string> warnings)
    {
        var limited = query.TopK is > 0 ? ranked.Take(query.TopK.Value).ToList() : ranked.ToList();

        var budget = query.MaxTokens;
        var tokensUsed = 0;
        var taken = new List<SearchMatch>();
        var truncated = false;
        foreach (var match in limited)
        {
            if (budget is > 0 && taken.Count > 0 && tokensUsed + TokenCounter.Estimate(match.Snippet) > budget)
            {
                truncated = true;
                break;
            }

            tokensUsed += TokenCounter.Estimate(match.Snippet);
            taken.Add(match);
        }

        return new SearchResponse(taken, tokensUsed, truncated) { Warnings = warnings };
    }
}
