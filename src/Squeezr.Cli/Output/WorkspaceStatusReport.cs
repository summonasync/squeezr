using Squeezr.Core.Indexing.Ignore;
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
    string? EmbeddingsProvider = null,
    IReadOnlyList<string> Warnings = default!)
{
    public static WorkspaceStatusReport Create(
        WorkspaceStatus status, IReadOnlyList<string> staleFiles, string? embeddingsProvider = null)
    {
        var warnings = new List<string>();
        var repos = status.Repos
            .Select(r =>
            {
                var ignored = BuildIgnoredReport(r, warnings);
                return new RepoStatusReport(
                    r.Name,
                    r.Path,
                    r.Files,
                    r.Symbols,
                    r.Chunks,
                    staleFiles.Where(k => k.StartsWith(r.Name + "/", StringComparison.Ordinal)).ToList(),
                    r.LastIndexedAt,
                    Age(r.LastIndexedAt),
                    ignored);
            })
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
            embeddingsProvider,
            warnings);
    }

    private static RepoIgnoredReport? BuildIgnoredReport(RepoStatus repo, List<string> warnings)
    {
        if (repo.IgnoredTotal == 0 || repo.IgnoredBySource is null || repo.IgnoredBySource.Count == 0)
            return null;

        var dominant = repo.IgnoredBySource.MaxBy(kv => kv.Value);
        if (dominant.Key is not null && dominant.Value > 0 && IgnoreSourceKey.IsKnown(dominant.Key))
        {
            warnings.Add(FormatIgnoreWarning(dominant.Key, dominant.Value));
        }

        var bySourceData = new Dictionary<string, int>();
        foreach (var (source, count) in repo.IgnoredBySource.OrderBy(kv => kv.Key))
        {
            if (count > 0)
                bySourceData[source] = count;
        }

        return new RepoIgnoredReport(repo.IgnoredTotal, bySourceData);
    }

    private static string FormatIgnoreWarning(string sourceKey, int count)
    {
        var via = sourceKey switch
        {
            "defaults" => "built-in defaults",
            "config" => "indexing.ignore config",
            "squeezrignore" => ".squeezrignore",
            "gitignore" => ".gitignore",
            _ => sourceKey
        };
        var hint = sourceKey switch
        {
            "gitignore" => " — set indexing.respectGitignore=false to disable",
            "squeezrignore" => " — remove .squeezrignore or adjust patterns to include them",
            "config" => " — adjust indexing.ignore in the workspace config",
            _ => " — adjust indexing.ignore defaults via workspace config"
        };
        return $"last index pass excluded {count} file(s) via {via}{hint}";
    }

    private static TimeSpan? Age(DateTime? indexedAt) =>
        indexedAt is null ? null : DateTime.UtcNow - indexedAt.Value.ToUniversalTime();
}
