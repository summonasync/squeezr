using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Core.Indexing.Query;

public sealed record SearchResponse(
    IReadOnlyList<SearchMatch> Matches,
    int TokensUsed,
    bool Truncated,
    TruncationHint? Hint = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
