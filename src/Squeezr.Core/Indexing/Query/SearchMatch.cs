namespace Squeezr.Core.Indexing.Query;

public sealed record SearchMatch(
    string Repo,
    string Path,
    int Line,
    double Score,
    string Snippet);
