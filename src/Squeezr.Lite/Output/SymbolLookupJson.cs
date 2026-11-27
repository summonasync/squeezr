using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Lite.Output;

public sealed record SymbolLookupJson(
    IReadOnlyList<SymbolHit> Matches,
    int TokensUsed,
    bool Truncated,
    string? Hint,
    IReadOnlyList<string>? DidYouMean,
    IReadOnlyList<string>? GuardWarnings,
    string State)
{
    public static SymbolLookupJson From(SymbolLookupResult result)
    {
        var clipped = result.State is AnswerState.Clipped;
        return new(
            result.Matches,
            result.TokensUsed,
            clipped,
            clipped ? result.RemedyForCli() : null,
            result.DidYouMean,
            result.GuardWarnings,
            result.State.StateName());
    }
}
