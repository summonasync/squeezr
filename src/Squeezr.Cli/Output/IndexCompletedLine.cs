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
    IReadOnlyList<RepoIndexResult> Repos,
    string? Provider = null,
    int DeferredReaps = 0,
    int PendingReaps = 0,
    int GuardWithheld = 0,
    int GuardDropped = 0,
    int Embedded = 0,
    int Throttled = 0)
{
    public static IndexCompletedLine FromResult(IndexResult result, int durationMs, int? symbols, string? provider = null) =>
        new(
            "completed",
            durationMs,
            result.Repos.Sum(repo => repo.Files),
            result.Repos.Sum(repo => repo.Added),
            result.Repos.Sum(repo => repo.Modified),
            result.Repos.Sum(repo => repo.Deleted),
            symbols is > 0 ? symbols : null,
            result.Repos,
            provider,
            result.DeferredReaps,
            result.PendingReaps,
            result.GuardWithheld,
            result.GuardDropped,
            result.Embedded,
            result.Throttled);
}
