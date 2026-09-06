namespace Squeezr.Core.Indexing.Query;

public sealed record SearchResponse(IReadOnlyList<SearchMatch> Matches, int TokensUsed, bool Truncated);
