using System.Text.Json;

namespace Squeezr.Core.Storage;

public sealed record ToolStats(string Tool, int Count, double AvgTokensUsed, double TruncationRate);

public sealed record SavingsStats(
    long ActualTokens,
    long CounterfactualTokens,
    long SavedTokens,
    double Ratio,
    int UnknownFiles);

public sealed record CacheStats(int Hits, int Misses, double HitRate, int SessionsWithCache);

public sealed record QueryStatsSummary(
    int TotalRequests,
    IReadOnlyList<ToolStats> ByTool,
    SavingsStats Savings,
    CacheStats Cache,
    DateTime? From,
    DateTime? To,
    int Records);

public static class QueryTelemetrySummary
{
    public static QueryStatsSummary Summarize(IReadOnlyList<QueryTelemetryEntry> entries, string workspaceRoot)
    {
        var byTool = entries
            .GroupBy(e => e.Tool)
            .Select(g => new ToolStats(
                g.Key,
                g.Count(),
                g.Average(e => e.TokensUsed),
                g.Average(e => e.Truncated ? 1.0 : 0.0)))
            .OrderBy(t => t.Tool, StringComparer.Ordinal)
            .ToList();

        var (counterfactual, unknownFiles) = EstimateCounterfactual(entries, workspaceRoot);
        var actual = entries.Sum(e => (long)e.TokensUsed);
        var saved = Math.Max(0, counterfactual - actual);
        var ratio = counterfactual == 0 || actual == 0 ? 0 : counterfactual / (double)actual;

        var hits = entries.Count(e => e.CacheHit);
        var hitRate = entries.Count == 0 ? 0 : hits / (double)entries.Count;

        return new QueryStatsSummary(
            entries.Count,
            byTool,
            new SavingsStats(actual, counterfactual, saved, ratio, unknownFiles),
            new CacheStats(hits, entries.Count - hits, hitRate, DistinctCacheSessions(entries)),
            entries.Count == 0 ? null : entries.Min(e => e.Ts),
            entries.Count == 0 ? null : entries.Max(e => e.Ts),
            entries.Count);
    }

    private static (long Counterfactual, int UnknownFiles) EstimateCounterfactual(
        IReadOnlyList<QueryTelemetryEntry> entries, string workspaceRoot)
    {
        using var store = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(workspaceRoot));
        var indexedFiles = store?.ListAllFiles();

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        long counterfactual = 0;
        var unknownFiles = 0;
        foreach (var entry in entries)
        {
            if (entry.Tool is not ("expand" or "expand_span"))
            {
                continue;
            }

            var file = ExtractFile(entry.Params);
            if (file is null)
            {
                continue;
            }

            string canonical;
            try
            {
                canonical = Path.GetFullPath(file, workspaceRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                unknownFiles++;
                continue;
            }

            if (!seenPaths.Add(canonical))
            {
                continue;
            }

            var indexed = indexedFiles is null ? null : FindByPath(indexedFiles, file);
            if (indexed is null)
            {
                unknownFiles++;
                continue;
            }

            counterfactual += Math.Max(1, (int)Math.Ceiling(indexed.SizeBytes / 4.0 * 1.1));
        }

        return (counterfactual, unknownFiles);
    }

    private static string? ExtractFile(object? parameters)
    {
        if (parameters is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("file", out var file))
            {
                return null;
            }

            return file.ValueKind == JsonValueKind.String ? file.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileDocument? FindByPath(IReadOnlyList<FileDocument> indexedFiles, string requested)
    {
        var normalized = requested.Replace('\\', '/').TrimStart('/');
        return indexedFiles.FirstOrDefault(f =>
            string.Equals(f.Path, normalized, StringComparison.Ordinal) ||
            normalized.EndsWith('/' + f.Path, StringComparison.Ordinal));
    }

    private static int DistinctCacheSessions(IReadOnlyList<QueryTelemetryEntry> entries) =>
        entries.Where(e => e.CacheHit).Select(e => e.SessionId).Distinct().Count();
}
