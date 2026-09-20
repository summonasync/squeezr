using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

public sealed record WorkspaceStatusReport(
    string Name,
    int Files,
    int Symbols,
    int Chunks,
    IReadOnlyList<string> StaleFiles,
    TimeSpan? IndexAge,
    IReadOnlyList<RepoStatusReport> Repos,
    QdrantStatus? Qdrant,
    string? EmbeddingsProvider = null)
{
    public static WorkspaceStatusReport Create(
        WorkspaceStatus status, IReadOnlyList<string> staleFiles, string? embeddingsProvider = null)
    {
        var repos = status.Repos
            .Select(r => new RepoStatusReport(
                r.Name,
                r.Path,
                r.Files,
                r.Symbols,
                r.Chunks,
                staleFiles.Where(k => k.StartsWith(r.Name + "/", StringComparison.Ordinal)).ToList(),
                r.LastIndexedAt,
                Age(r.LastIndexedAt)))
            .ToList();

        var lastIndexedAt = status.Repos.Select(r => r.LastIndexedAt).Max();
        return new WorkspaceStatusReport(
            status.Name,
            status.Repos.Sum(r => r.Files),
            status.Repos.Sum(r => r.Symbols),
            status.Repos.Sum(r => r.Chunks),
            staleFiles,
            Age(lastIndexedAt),
            repos,
            status.Qdrant,
            embeddingsProvider);
    }

    private static TimeSpan? Age(DateTime? indexedAt) =>
        indexedAt is null ? null : DateTime.UtcNow - indexedAt.Value.ToUniversalTime();
}
