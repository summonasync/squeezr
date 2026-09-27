namespace Squeezr.Core.Indexing.Query;

public sealed record SearchQuery(
    string Query,
    bool Regex,
    bool? IgnoreCase,
    string? Repo,
    string? PathGlob,
    string? Language,
    int? TopK,
    int? MaxTokens,
    string? Prefix = null);
