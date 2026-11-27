using System.Text.Json.Serialization;
using Squeezr.Lite.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp;

public sealed record ToolEnvelope(
    [property: JsonPropertyName("data")] object? Data,
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("indexAge")] TimeSpan? IndexAge,
    [property: JsonPropertyName("staleFiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? StaleFiles,
    [property: JsonPropertyName("didYouMean"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DidYouMean = null,
    [property: JsonPropertyName("definitions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RefDefinitionData>? Definitions = null,
    [property: JsonPropertyName("cost")] AnswerCost? Cost = null,
    [property: JsonPropertyName("freshness"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AnswerFreshness? Freshness = null,
    [property: JsonPropertyName("subject"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AnswerSubjectBody? Subject = null)
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
        AnswerFreshness? freshness = null)
    {
        var lines = warnings ?? [];
        if (workspaceRoot is not null)
        {
            lines = [.. lines, .. ReconcilePendingNotice.Lines(ReconcilePendingNotice.Read(workspaceRoot))];
        }

        return new(data, tokensUsed, truncated, lines, ResolveIndexAge(workspaceRoot), staleFiles,
            didYouMean, definitions, cost, freshness);
    }

    public ToolEnvelope WithWarning(string? warning) =>
        warning is null ? this : this with { Warnings = [.. Warnings, warning] };

    public ToolEnvelope WithSubject(AnswerSubjectBody? subject, string? crossTreeWarning) =>
        subject is null
            ? this
            : this with
            {
                Subject = Subject ?? subject,
                Warnings = crossTreeWarning is null ||
                           Warnings.Contains(crossTreeWarning, StringComparer.Ordinal)
                    ? Warnings
                    : [.. Warnings, crossTreeWarning],
            };

    [JsonIgnore]
    public int PaidTokens => TokensUsed + (Freshness?.FreshnessTokens ?? 0);

    public ToolEnvelope MarkReplayed() => this;

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

public sealed record RefDefinitionData(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine);
