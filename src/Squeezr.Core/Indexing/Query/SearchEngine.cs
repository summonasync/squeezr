using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Query;

public sealed class SearchEngine
{
    public const string VectorStoreUnavailableWarning = "vector store is unavailable, falling back to exact search only";

    private readonly string _workspaceRoot;
    private readonly RankExclusion _rankExclusion;

    public SearchEngine(string workspaceRoot, RankExclusion? rankExclusion = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _rankExclusion = rankExclusion ?? RankExclusion.None;
    }

    public SearchResponse Search(SearchQuery query)
    {
        var fileScope = ResolveFileScope(query);
        var matches = ExactSearchPlan.Execute(_workspaceRoot, query, trackChunkKeys: false, fileScope)
            .Select(hit => hit.Match)
            .ToList();
        return Finalize(matches, query, []);
    }

    public async Task<SearchResponse> SearchHybridAsync(
        SearchQuery query,
        IEmbeddingProvider? embeddings,
        IVectorStore? vectorStore,
        CancellationToken ct,
        SemanticCoverage? coverage = null)
    {
        var fileScope = ResolveFileScope(query);
        var exact = ExactSearchPlan.Execute(_workspaceRoot, query, trackChunkKeys: true, fileScope);
        var identifierHits = query.Regex
            ? []
            : IdentifierTokenLeg.Execute(_workspaceRoot, query, fileScope);
        var semantic = new List<(string Key, SearchMatch Match)>();
        var warnings = new List<string>();

        if (vectorStore is not null && embeddings is not null)
        {
            try
            {
                if (!await vectorStore.IsAvailableAsync(ct))
                {
                    warnings.Add(VectorStoreUnavailableWarning);
                }
                else if (await vectorStore.CountAsync(ct) > 0)
                {
                    semantic = await SemanticHitsResolver.Resolve(_workspaceRoot, query, embeddings, vectorStore, ct, fileScope);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"vector search failed ({ex.Message}), falling back to exact search only");
            }

            if (coverage is { TotalChunks: > 0, PendingChunks: > 0 })
            {
                warnings.Add(FormatCoverageWarning(coverage));
            }
        }

        var phraseLocations = new HashSet<(string Path, int Line)>(
            exact.Select(hit => (hit.Match.Path, hit.Match.Line)));
        var dedupedIdentifier = identifierHits
            .Where(hit => !phraseLocations.Contains((hit.Match.Path, hit.Match.Line)))
            .ToList();

        var exactRanked = exact
            .Concat(dedupedIdentifier)
            .Select(hit => (hit.Key!, hit.Match))
            .ToList();
        var merged = RrfMerger.Merge(exactRanked, semantic);
        return Finalize(merged, query, warnings);
    }

    private HashSet<uint>? ResolveFileScope(SearchQuery query)
    {
        var prefix = PathPrefix.Normalize(query.Prefix);
        var excluding = _rankExclusion.IsActive;
        if (prefix is null && !excluding)
        {
            return null;
        }

        // an unbuilt index is ExactSearchPlan's error to report, with its canonical index_not_built code
        if (!File.Exists(SqueezrPaths.TrigramIndexPath(_workspaceRoot)))
        {
            return null;
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        uint? repoId = null;
        if (query.Repo is not null)
        {
            var repo = store.FindRepo(query.Repo);
            if (repo is null)
            {
                return [];
            }

            repoId = repo.Id;
        }

        if (prefix is null)
        {
            return FileScope.Resolve(store, path => !_rankExclusion.Matches(path), repoId);
        }

        var scope = FileScope.Resolve(
            store,
            path => PathPrefix.Matches(prefix, path) && !_rankExclusion.Matches(path),
            repoId);
        if (scope.Count > 0)
        {
            return scope;
        }

        // rank-excluded files stay indexed, so a prefix whose whole subtree is excluded answers
        // with no hits — only a prefix matching nothing at all is the "no such subtree" error
        if (!excluding || FileScope.Resolve(store, path => PathPrefix.Matches(prefix, path), repoId).Count == 0)
        {
            var indexedPaths = (repoId is { } id ? store.ListFiles(id) : store.ListAllFiles())
                .Select(f => f.Path);
            throw PathPrefix.NoSubtreeException(prefix, indexedPaths);
        }

        return scope;
    }

    private static string FormatCoverageWarning(SemanticCoverage coverage)
    {
        var percent = (int)Math.Round(coverage.Ratio!.Value * 100, MidpointRounding.AwayFromZero);
        var noun = coverage.PendingChunks == 1 ? "chunk" : "chunks";
        return $"semantic coverage: {percent}% ({coverage.PendingChunks} {noun} pending embedding)";
    }

    private static SearchResponse Finalize(IReadOnlyList<SearchMatch> ranked, SearchQuery query, IReadOnlyList<string> warnings)
    {
        // collapse before the cap so topK counts files, not spans of one file
        var files = FileHitCollapser.Collapse(ranked);
        var limited = query.TopK is > 0 ? files.Take(query.TopK.Value).ToList() : files.ToList();

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
