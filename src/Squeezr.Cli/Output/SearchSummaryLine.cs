using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Cli.Output;

public sealed record SearchSummaryLine(
    string Type,
    int Matches,
    int TokensUsed,
    int? MaxTokens,
    bool Truncated,
    TruncationHint? Hint = null,
    int? ExactTokens = null,
    int? SemanticTokens = null);
