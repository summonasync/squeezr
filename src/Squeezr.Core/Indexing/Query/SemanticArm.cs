namespace Squeezr.Core.Indexing.Query;

public sealed record SemanticArm(
    IReadOnlyList<(string Key, SearchMatch Match)> Hits,
    int RequestedTopK,
    int StoreCandidates,
    int InScopeCandidates)
{
    public static SemanticArm Empty { get; } = new([], 0, 0, 0);
}
