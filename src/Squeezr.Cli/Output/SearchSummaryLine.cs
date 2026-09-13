namespace Squeezr.Cli.Output;

public sealed record SearchSummaryLine(
    string Type,
    int Matches,
    int TokensUsed,
    int? MaxTokens,
    bool Truncated);
