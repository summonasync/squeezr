using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Query;

public static class SemanticHitsResolver
{
    private const int VectorTopK = 20;

    public static async Task<List<(string Key, SearchMatch Match)>> Resolve(
        string workspaceRoot,
        SearchQuery query,
        IEmbeddingProvider embeddings,
        IVectorStore vectorStore,
        CancellationToken ct)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));

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

        var vectors = await embeddings.EmbedAsync([query.Query], ct);
        if (vectors.Length == 0)
        {
            return [];
        }

        var hits = await vectorStore.SearchAsync(vectors[0], VectorTopK, repoId, ct);
        var reposById = store.ListRepos().ToDictionary(r => r.Id);

        var result = new List<(string Key, SearchMatch Match)>();
        foreach (var hit in hits)
        {
            if (!reposById.TryGetValue(hit.RepoId, out var repo))
            {
                continue;
            }

            var file = store.FindFileById(hit.FileId);
            if (file is null)
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
                new SearchMatch(repo.Name, file.Path, hit.StartLine, hit.Score, chunk.Text.Trim())
                {
                    Source = "semantic"
                }));
        }

        return result;
    }
}
