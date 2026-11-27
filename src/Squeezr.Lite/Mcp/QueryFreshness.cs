using Squeezr.Core.Indexing;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp;

public static class QueryFreshness
{
    public static IReadOnlyList<(string Repo, string Path)> Candidates(
        IEnumerable<(string Repo, string Path)> namedFiles) =>
        [.. namedFiles.Take(FreshnessGuard.CandidateLimit).Distinct()];

    public static FreshnessResult Guard(
        string root,
        IReadOnlyList<(string Repo, string Path)> candidates,
        CancellationToken cancellationToken) =>
        FreshnessGuard.CheckAndRefresh(root, candidates, StaleLockWarningSink.ToStderr, cancellationToken);
}
