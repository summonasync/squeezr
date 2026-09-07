using System.Text.Json.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

public sealed record ToolEnvelope(
    [property: JsonPropertyName("data")] object? Data,
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("indexAge")] TimeSpan? IndexAge,
    [property: JsonPropertyName("staleFiles")] IReadOnlyList<string> StaleFiles)
{
    public static ToolEnvelope Wrap(
        object? data,
        int tokensUsed,
        bool truncated,
        IReadOnlyList<string>? warnings = null,
        string? workspaceRoot = null,
        IReadOnlyList<string>? staleFiles = null) =>
        new(data, tokensUsed, truncated, warnings ?? [], ResolveIndexAge(workspaceRoot), staleFiles ?? []);

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
