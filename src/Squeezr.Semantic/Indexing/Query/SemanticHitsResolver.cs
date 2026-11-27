using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Semantic.Indexing.Query;

public static class SemanticHitsResolver
{
    public static async Task<SemanticArm> Resolve(
        string workspaceRoot,
        SearchQuery query,
        IEmbeddingProvider embeddings,
        IVectorStore vectorStore,
        CancellationToken ct,
        IReadOnlySet<uint>? fileScope,
        SecretGuardReader guard)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));

        uint? repoId = null;
        if (query.Repo is not null)
        {
            var repo = store.FindRepoIgnoringCase(query.Repo);
            if (repo is null)
            {
                return SemanticArm.Empty;
            }

            repoId = repo.Id;
        }

        var vectors = await embeddings.EmbedAsync([query.Query], EmbeddingUsage.Query, ct);
        if (vectors.Length == 0)
        {
            return SemanticArm.Empty;
        }

        var requestedTopK = fileScope is null
            ? ScopedVectorBudget.BaseTopK
            : ScopedVectorBudget.For(fileScope.Count, UniverseFiles(store, repoId));
        var hits = await vectorStore.SearchAsync(
            vectors[0],
            requestedTopK,
            new VectorSearchFilter(repoId, DocKindDetector.FilterKind(query.Kind)),
            ct);
        var reposById = store.ListRepos().ToDictionary(r => r.Id);

        var result = new List<(string Key, SearchMatch Match)>();
        var inScopeCandidates = 0;
        foreach (var hit in hits)
        {
            if (fileScope is not null && !fileScope.Contains(hit.FileId))
            {
                continue;
            }

            inScopeCandidates++;
            if (!reposById.TryGetValue(hit.RepoId, out var repo))
            {
                continue;
            }

            var file = store.FindFileById(hit.FileId);
            if (file is null || guard.TryWithhold(file.Path))
            {
                continue;
            }

            var chunk = store.ListChunksByFile(hit.FileId)
                .FirstOrDefault(c => c.StartLine == hit.StartLine && c.EndLine == hit.EndLine);
            if (chunk is null)
            {
                continue;
            }

            result.Add((
                $"{hit.FileId}:{chunk.StartLine}",
                new SearchMatch(repo.Name, file.Path, hit.StartLine, hit.Score, SemanticSnippet.Build(chunk.Text, query.Query))
                {
                    Source = "semantic"
                }));

            if (result.Count == ScopedVectorBudget.BaseTopK)
            {
                break;
            }
        }

        return new SemanticArm(result, requestedTopK, hits.Count, inScopeCandidates);
    }

    private static int UniverseFiles(MetaStore store, uint? repoId) =>
        repoId is { } id ? store.CountFiles(id) : store.CountAllFiles();
}
