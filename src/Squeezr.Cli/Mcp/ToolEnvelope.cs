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
    IReadOnlyList<FileFreshness>? FileFreshness = null)
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
        IReadOnlyList<FileFreshness>? fileFreshness = null)
    {
        var resolved = cost ?? AnswerCost.Unknown(tokensUsed, !truncated);
        return new(data, tokensUsed, truncated, warnings ?? [], ResolveIndexAge(workspaceRoot), staleFiles ?? [],
            didYouMean, definitions, resolved with { TokensUsed = tokensUsed, Complete = !truncated }, fileFreshness);
    }

    public ToolEnvelope WithWarning(string? warning) =>
        warning is null ? this : this with { Warnings = [.. Warnings, warning] };

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
