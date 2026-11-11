using System.Text.Json.Serialization;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

public sealed record ToolEnvelope(
    [property: JsonPropertyName("data")] object? Data,
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("indexAge")] TimeSpan? IndexAge,
    [property: JsonPropertyName("staleFiles")] IReadOnlyList<string> StaleFiles,
    [property: JsonPropertyName("didYouMean"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DidYouMean = null,
    [property: JsonPropertyName("definitions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RefDefinitionData>? Definitions = null,
    [property: JsonPropertyName("cost")] AnswerCost? Cost = null,
    [property: JsonPropertyName("fileFreshness"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<FileFreshness>? FileFreshness = null,
    [property: JsonPropertyName("freshness"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AnswerFreshness? Freshness = null)
{
    public static ToolEnvelope Wrap(
        object? data,
        int tokensUsed,
        bool truncated,
        IReadOnlyList<string>? warnings = null,
        string? workspaceRoot = null,
        IReadOnlyList<string>? staleFiles = null,
        IReadOnlyList<string>? didYouMean = null,
        IReadOnlyList<RefDefinitionData>? definitions = null,
        AnswerCost? cost = null,
        IReadOnlyList<FileFreshness>? fileFreshness = null,
        AnswerFreshness? freshness = null)
    {
        // a surface that measured its own cost owns the complete label: an answer that substituted a member
        // map for a refused body lost nothing the caller asked for, and a recomputed flag would call it cut.
        // The freshness rule therefore only ever withholds completeness — a check that found more of the
        // verdict than this answer names is not a whole claim — and never restores it from `truncated`, so
        // the three states 115 froze and the coverage rule 017 froze compose instead of undoing each other
        var resolved = cost ?? AnswerCost.Unknown(tokensUsed, !truncated);
        var complete = resolved.Complete && (freshness is null || freshness.CarriesWholeVerdict);
        return new(data, tokensUsed, truncated, warnings ?? [], ResolveIndexAge(workspaceRoot), staleFiles ?? [],
            didYouMean, definitions, resolved with { TokensUsed = tokensUsed, Complete = complete }, fileFreshness,
            freshness);
    }

    public ToolEnvelope WithWarning(string? warning) =>
        warning is null ? this : this with { Warnings = [.. Warnings, warning] };

    // what the caller actually pays for this answer: the budgeted content plus the freshness story the
    // envelope ships beside it. telemetry reads the answer through this number, so stats cannot come out
    // cheaper than the bytes that went down the pipe
    [JsonIgnore]
    public int PaidTokens => TokensUsed + (Freshness?.FreshnessTokens ?? 0);

    // the stored envelope describes the call that produced it; a replay says so in the block that attests
    // the check, and an answer with no check of its own has nothing to mark
    public ToolEnvelope MarkReplayed() =>
        Freshness is null ? this : this with { Freshness = Freshness.MarkReplayed() };

    private static TimeSpan? ResolveIndexAge(string? workspaceRoot)
    {
        if (workspaceRoot is null)
        {
            return null;
        }

        var metaDbPath = SqueezrPaths.MetaDbPath(workspaceRoot);
        if (!File.Exists(metaDbPath))
        {
            return null;
        }

        return DateTime.UtcNow - File.GetLastWriteTimeUtc(metaDbPath);
    }
}
