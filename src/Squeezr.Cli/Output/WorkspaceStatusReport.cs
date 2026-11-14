using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Storage;
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
    IReadOnlyList<string> Warnings = default!,
    IReadOnlyList<WorkspaceSiblingEntry>? Workspaces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int WorkspacesOmitted = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, int>? KindChunks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FleetWatchAdoption? WatchAdoption = null,
    // What this store says about its own reconcile state, as data: a reader that has to decide whether the
    // numbers below describe this checkout gets the answer without parsing the warning line. Absent is the
    // state a workspace nobody filled from a main worktree carries, not a pending one.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeRecord? Reconcile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EmbeddingModelKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? OtherModelChunks = null)
{
    public static WorkspaceStatusReport Create(
        WorkspaceStatus status,
        IReadOnlyList<string> staleFiles,
        string? embeddingsProvider = null,
        WorkspaceSiblingSnapshot? siblings = null,
        FleetWatchAdoption? watchAdoption = null,
        WorktreeRecord? reconcile = null,
        string? embeddingModelKey = null,
        int? otherModelChunks = null)
    {
        var warnings = new List<string>();
        warnings.AddRange(ReconcilePendingNotice.Lines(reconcile));
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
                    ignored,
                    RepoGuardReport.FromRepo(r));
            })
            .ToList();

        var guard = RepoGuardReport.FromTally(status.Repos);
        if (guard is not null)
        {
            warnings.Add(guard.Warning());
        }

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
            warnings,
            ListedWorkspaces(siblings),
            siblings?.Omitted ?? 0,
            WatchAdoption: watchAdoption,
            Reconcile: reconcile,
            EmbeddingModelKey: embeddingModelKey,
            OtherModelChunks: otherModelChunks);
    }

    private static IReadOnlyList<WorkspaceSiblingEntry>? ListedWorkspaces(WorkspaceSiblingSnapshot? siblings) =>
        siblings?.Workspaces is { Count: > 0 } listed ? listed : null;

    private static RepoIgnoredReport? BuildIgnoredReport(RepoStatus repo, List<string> warnings)
    {
        var hasIgnored = repo.IgnoredTotal > 0 && repo.IgnoredBySource is { Count: > 0 };
        if (!hasIgnored && repo.TrackedButExcluded == 0)
            return null;

        var bySourceData = new Dictionary<string, int>();
        if (hasIgnored && repo.IgnoredBySource is { } ignoredBySource)
        {
            var dominant = ignoredBySource.MaxBy(kv => kv.Value);
            if (dominant.Key is not null && dominant.Value > 0 && IgnoreSourceKey.IsKnown(dominant.Key))
            {
                warnings.Add(FormatIgnoreWarning(dominant.Key, dominant.Value));
            }

            foreach (var (source, count) in ignoredBySource.OrderBy(kv => kv.Key))
            {
                if (count > 0)
                    bySourceData[source] = count;
            }
        }

        if (repo.TrackedButExcluded > 0)
        {
            warnings.Add(FormatTrackedButExcludedWarning(repo.TrackedButExcluded));
        }

        return new RepoIgnoredReport(hasIgnored ? repo.IgnoredTotal : 0, bySourceData, repo.TrackedButExcluded);
    }

    private static string FormatTrackedButExcludedWarning(int count) =>
        $"last pass kept {count} file(s) that .gitignore names but git tracks — tracked files are never " +
        "gitignore-dropped; 'git rm --cached <path>' or narrow the patterns if they should not be searched";

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
