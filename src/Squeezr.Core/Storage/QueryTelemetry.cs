using System.Text.Json;
using System.Text.Json.Nodes;
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
    [property: JsonPropertyName("indexAgeSeconds")] long? IndexAgeSeconds,
    [property: JsonPropertyName("client"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client = null,
    [property: JsonPropertyName("clientVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientVersion = null,
    [property: JsonPropertyName("cacheInvalidated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CacheInvalidated = false);

public static class QueryTelemetry
{
    // what a recorded parameter value becomes when the guard says it names a protected path — the line stays
    // (the counters over queries.jsonl are the reason the file exists), only the name leaves it
    public const string RedactedPlaceholder = "[secret-guard withheld]";

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
        TimeSpan? indexAge = null,
        string? client = "cli",
        string? clientVersion = null,
        bool cacheInvalidated = false) =>
        Append(workspaceRoot, new QueryTelemetryEntry(
            DateTime.UtcNow, sessionId, tool, parameters, tokensUsed, truncated,
            hits, durationMs, cacheHit, indexAge is null ? null : (long)indexAge.Value.TotalSeconds,
            client, clientVersion, cacheInvalidated));

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

    // Recorded parameters are the one place where a query that ran before the guard existed keeps quoting the
    // path it asked about, so the purge pass rewrites them through the same classifier that refused the read.
    // Only values the classifier calls a protected path are replaced; lines it cannot parse and values it does
    // not recognise are written back byte for byte, which is what keeps the counters over queries.jsonl honest.
    public static int RedactProtectedParams(string workspaceRoot, Func<string, bool> isProtectedPath)
    {
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (!File.Exists(path))
        {
            return 0;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var redacted = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(lines[i]) is not JsonObject entry)
                {
                    continue;
                }

                if (entry["params"] is null)
                {
                    continue;
                }

                if (RedactTree(entry["params"]!, isProtectedPath, ref redacted) > 0)
                {
                    lines[i] = entry.ToJsonString();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (redacted == 0)
        {
            return 0;
        }

        var tempPath = path + ".scrub.tmp";
        try
        {
            // joined explicitly so a scrub run on one platform does not rewrite the line endings of a log
            // another platform appended
            File.WriteAllText(tempPath, string.Join('\n', lines) + '\n');
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            return 0;
        }

        return redacted;
    }

    private static int RedactTree(JsonNode node, Func<string, bool> isProtectedPath, ref int redacted)
    {
        var hits = 0;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (NamesAProtectedPath(property.Value, isProtectedPath))
                {
                    obj[property.Key] = JsonValue.Create(RedactedPlaceholder);
                    redacted++;
                    hits++;
                    continue;
                }

                if (property.Value is not null)
                {
                    hits += RedactTree(property.Value, isProtectedPath, ref redacted);
                }
            }

            return hits;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (NamesAProtectedPath(array[i], isProtectedPath))
                {
                    array[i] = JsonValue.Create(RedactedPlaceholder);
                    redacted++;
                    hits++;
                    continue;
                }

                if (array[i] is not null)
                {
                    hits += RedactTree(array[i]!, isProtectedPath, ref redacted);
                }
            }
        }

        return hits;
    }

    private static bool NamesAProtectedPath(JsonNode? node, Func<string, bool> isProtectedPath) =>
        node is JsonValue value && value.TryGetValue(out string? text) && isProtectedPath(text);

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
