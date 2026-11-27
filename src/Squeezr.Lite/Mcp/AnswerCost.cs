using System.Text.Json.Serialization;

namespace Squeezr.Lite.Mcp;

public sealed record AnswerCost(
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("baselineTokens")] long? BaselineTokens,
    [property: JsonPropertyName("savedTokens")] long? SavedTokens,
    [property: JsonPropertyName("ratio")] double? Ratio,
    [property: JsonPropertyName("complete")] bool Complete)
{
    public static AnswerCost Unknown(int tokensUsed, bool complete) =>
        new(tokensUsed, null, null, null, complete);

    public static AnswerCost Measured(int tokensUsed, long baselineTokens, bool complete) => new(
        tokensUsed,
        Math.Max(1, baselineTokens),
        baselineTokens - tokensUsed,
        tokensUsed == 0 ? null : baselineTokens / (double)tokensUsed,
        complete);
}
