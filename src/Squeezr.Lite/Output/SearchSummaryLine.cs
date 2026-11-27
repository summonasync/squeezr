using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Lite.Output;

public sealed record SearchSummaryLine(
    string Type,
    int Matches,
    int TokensUsed,
    int? MaxTokens,
    bool Truncated,
    TruncationHint? Hint = null,
    int? ExactTokens = null);
