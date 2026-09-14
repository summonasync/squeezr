using Squeezr.Core.Indexing;

namespace Squeezr.Cli.Output;

public sealed record IndexCompletedLine(
    string Type,
    int DurationMs,
    int Files,
    int Added,
    int Modified,
    int Deleted,
    int? Symbols,
    IReadOnlyList<RepoIndexResult> Repos)
{
    public static IndexCompletedLine FromResult(IndexResult result, int durationMs, int? symbols) =>
        new(
            "completed",
            durationMs,
            result.Repos.Sum(repo => repo.Files),
            result.Repos.Sum(repo => repo.Added),
            result.Repos.Sum(repo => repo.Modified),
            result.Repos.Sum(repo => repo.Deleted),
            symbols is > 0 ? symbols : null,
            result.Repos);
}
