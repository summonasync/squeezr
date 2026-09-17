using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squeezr.Core.Storage;

public sealed record QueryTelemetryEntry(
    [property: JsonPropertyName("ts")] DateTime Ts,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("params")] object? Params,
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("hits")] int Hits,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("cacheHit")] bool CacheHit,
    [property: JsonPropertyName("indexAgeSeconds")] long? IndexAgeSeconds);

public static class QueryTelemetry
{
    public static void Append(string workspaceRoot, QueryTelemetryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(SqueezrPaths.StoreDir(workspaceRoot));
            File.AppendAllText(SqueezrPaths.QueriesLogPath(workspaceRoot), JsonSerializer.Serialize(entry) + '\n');
        }
        catch
        {
        }
    }

    public static void Record(
        string workspaceRoot,
        Guid sessionId,
        string tool,
        object? parameters,
        int tokensUsed,
        bool truncated,
        int hits,
        long durationMs,
        bool cacheHit = false,
        TimeSpan? indexAge = null) =>
        Append(workspaceRoot, new QueryTelemetryEntry(
            DateTime.UtcNow, sessionId, tool, parameters, tokensUsed, truncated,
            hits, durationMs, cacheHit, indexAge is null ? null : (long)indexAge.Value.TotalSeconds));

    public static IReadOnlyList<QueryTelemetryEntry> ReadAll(string workspaceRoot)
    {
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var entries = new List<QueryTelemetryEntry>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<QueryTelemetryEntry>(line);
                    if (parsed is not null)
                    {
                        entries.Add(parsed);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static int Count(string workspaceRoot)
    {
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            return File.ReadLines(path).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
