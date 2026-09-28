using Squeezr.Core.Indexing.Query;

namespace Squeezr.Cli.Output;

public sealed record SearchMatchLine(
    string Type,
    string Repo,
    string Path,
    int Line,
    double Score,
    string Snippet,
    string Source,
    int? AlsoMatched)
{
    public static SearchMatchLine FromMatch(SearchMatch match) =>
        new("match", match.Repo, match.Path, match.Line, match.Score, match.Snippet, match.Source,
            match.AlsoMatched == 0 ? null : match.AlsoMatched);
}
