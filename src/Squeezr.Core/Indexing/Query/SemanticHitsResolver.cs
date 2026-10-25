using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Query;

public static class SemanticHitsResolver
{
    private const int VectorTopK = 20;

    // A scoped query drops out-of-scope hits post-hoc, so the vector query asks for more than
    // VectorTopK to still fill the semantic arm; bounded, unlike a fileId must-match filter
    // that would put the whole allowed set into the Qdrant payload.
    private const int ScopedVectorTopK = VectorTopK * 4;

    public static async Task<List<(string Key, SearchMatch Match)>> Resolve(
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
                return [];
            }

            repoId = repo.Id;
        }

        var vectors = await embeddings.EmbedAsync([query.Query], EmbeddingUsage.Query, ct);
        if (vectors.Length == 0)
        {
            return [];
        }

        var hits = await vectorStore.SearchAsync(
            vectors[0],
            fileScope is null ? VectorTopK : ScopedVectorTopK,
            new VectorSearchFilter(repoId, DocKindDetector.FilterKind(query.Kind)),
            ct);
        var reposById = store.ListRepos().ToDictionary(r => r.Id);

        var result = new List<(string Key, SearchMatch Match)>();
        foreach (var hit in hits)
        {
            if (fileScope is not null && !fileScope.Contains(hit.FileId))
            {
                continue;
            }

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

            if (result.Count == VectorTopK)
            {
                break;
            }
        }

        return result;
    }
}
