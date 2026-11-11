using System.Text.Json;
using Squeezr.Core.Indexing;

namespace Squeezr.Core.Storage;

// MappedRate is null rather than zero when no row of the tool carries a state: a rate over an empty
// denominator would read as "nothing was mapped" where the ledger actually says "nothing is known".
// UnsplitRows counts those stateless rows — written before the split, or by a surface that records none.
public sealed record ToolStats(
    string Tool,
    int Count,
    double AvgTokensUsed,
    double TruncationRate,
    double? MappedRate,
    int UnsplitRows);

public sealed record SavingsStats(
    long ActualTokens,
    long CounterfactualTokens,
    long SavedTokens,
    double Ratio,
    int UnknownFiles,
    string BaselineLabel,
    bool BaselineApplies);

public sealed record CacheStats(
    int Hits,
    int Misses,
    double HitRate,
    int SessionsWithCache,
    int Invalidations = 0,
    DateTime? LastInvalidation = null,
    int EligibleQueries = 0);

public sealed record AgentStats(string Client, string? ClientVersion, int Count, DateTime LastSeen, int Sessions = 0);

public sealed record SessionStats(
    string SessionId,
    string Client,
    int Count,
    DateTime LastSeen,
    IReadOnlyList<string> TopTools);

public sealed record QueryStatsSummary(
    int TotalRequests,
    IReadOnlyList<ToolStats> ByTool,
    SavingsStats Savings,
    CacheStats Cache,
    DateTime? From,
    DateTime? To,
    int Records,
    IReadOnlyList<AgentStats> Agents,
    IReadOnlyList<SessionStats> Sessions,
    IReadOnlyList<string>? GuardWarnings = null);

public static class QueryTelemetrySummary
{
    public const string WholeFileBaselineLabel = "whole-file (expanded files)";

    public static QueryStatsSummary Summarize(IReadOnlyList<QueryTelemetryEntry> entries, string workspaceRoot)
    {
        var byTool = entries
            .GroupBy(e => e.Tool)
            .Select(g => new ToolStats(
                g.Key,
                g.Count(),
                g.Average(e => e.TokensUsed),
                g.Average(e => e.Truncated ? 1.0 : 0.0),
                MappedRate(g),
                g.Count(e => e.Mapped is null)))
            .OrderBy(t => t.Tool, StringComparer.Ordinal)
            .ToList();

        var withBaseline = entries.Where(e => e.BaselineTokens is not null).ToList();
        var legacy = entries.Where(e => e.BaselineTokens is null).ToList();
        var recorded = withBaseline.Sum(e => e.BaselineTokens!.Value);
        var (fallback, unknownFiles, guardWarnings) = EstimateCounterfactual(legacy, workspaceRoot);
        var counterfactual = recorded + fallback;
        var actual = entries.Sum(e => (long)e.TokensUsed);
        var saved = Math.Max(0, counterfactual - actual);
        var ratio = counterfactual == 0 || actual == 0 ? 0 : counterfactual / (double)actual;
        var baselineApplies = counterfactual > actual;

        var cacheEligible = entries.Where(e => CacheEligibleTools.Includes(e.Tool)).ToList();
        var hits = cacheEligible.Count(e => e.CacheHit);
        var hitRate = cacheEligible.Count == 0 ? 0 : hits / (double)cacheEligible.Count;

        return new QueryStatsSummary(
            entries.Count,
            byTool,
            new SavingsStats(actual, counterfactual, saved, ratio, unknownFiles, WholeFileBaselineLabel, baselineApplies),
            SummarizeCache(cacheEligible, hits, hitRate),
            entries.Count == 0 ? null : entries.Min(e => e.Ts),
            entries.Count == 0 ? null : entries.Max(e => e.Ts),
            entries.Count,
            AggregateAgents(entries),
            AggregateSessions(entries),
            guardWarnings);
    }

    // the mapped half of the ledger only says something over the rows that recorded it
    private static double? MappedRate(IGrouping<string, QueryTelemetryEntry> rows)
    {
        var known = rows.Where(e => e.Mapped is not null).ToList();
        return known.Count == 0
            ? null
            : known.Average(e => e.Mapped!.Value ? 1.0 : 0.0);
    }

    public static IReadOnlyList<AgentStats> AggregateAgents(IReadOnlyList<QueryTelemetryEntry> entries) =>
        entries
            .GroupBy(e => (Client: e.Client ?? "unknown", Version: e.ClientVersion))
            .Select(g => new AgentStats(
                g.Key.Client,
                g.Key.Version,
                g.Count(),
                g.Max(e => e.Ts),
                g.Where(e => e.SessionId != Guid.Empty).Select(e => e.SessionId).Distinct().Count()))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Client, StringComparer.Ordinal)
            .ThenBy(a => a.ClientVersion, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<SessionStats> AggregateSessions(IReadOnlyList<QueryTelemetryEntry> entries) =>
        entries
            .GroupBy(e => e.SessionId)
            .Select(g =>
            {
                var last = g.MaxBy(e => e.Ts)!;
                var topTools = g
                    .GroupBy(e => e.Tool)
                    .Select(t => (Tool: t.Key, Count: t.Count()))
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Tool, StringComparer.Ordinal)
                    .Take(3)
                    .Select(t => t.Tool)
                    .ToList();
                return new SessionStats(
                    g.Key.ToString("N")[..8], last.Client ?? "unknown", g.Count(), last.Ts, topTools);
            })
            .OrderByDescending(s => s.LastSeen)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();

    // the guard that decides which rows leave the baseline is the same guard whose masks can be dead: when the
    // extension list cannot be read, protected files stay in the arithmetic, and only the reader's warnings say
    // so. The tally is withheld from stats — IsProtected is all this pass asks — so the degradation line is the
    // one that can appear here.
    private static (long Counterfactual, int UnknownFiles, IReadOnlyList<string>? GuardWarnings) EstimateCounterfactual(
        IReadOnlyList<QueryTelemetryEntry> entries, string workspaceRoot)
    {
        using var store = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(workspaceRoot));
        var indexedFiles = store?.ListAllFiles();
        var guard = SecretGuardReader.Load(workspaceRoot);
        var recordedPaths = GuardedPathForms.Load(workspaceRoot, guard);

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

            // the guard classifies by path shape, and a mask written against a repo root ('infra/VaultKeys.cs')
            // matches no other shape: the ledger keeps whatever the caller typed, so every shape it could mean
            // has to be asked before either bucket
            if (recordedPaths.NamesAProtectedPath(file))
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
            // the stored row is the one shape the mask was authored against — an alias nobody typed still dies here
            if (indexed is not null && guard.IsProtected(indexed.Path))
            {
                continue;
            }

            if (indexed is null)
            {
                unknownFiles++;
                continue;
            }

            counterfactual += TokenEstimate.WholeFile(indexed.SizeBytes);
        }

        return (counterfactual, unknownFiles, guard.Warnings);
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

    private static CacheStats SummarizeCache(IReadOnlyList<QueryTelemetryEntry> entries, int hits, double hitRate)
    {
        var invalidated = entries.Where(e => e.CacheInvalidated).Select(e => e.Ts).ToList();
        return new CacheStats(
            hits,
            entries.Count - hits,
            hitRate,
            DistinctCacheSessions(entries),
            invalidated.Count,
            invalidated.Count == 0 ? null : invalidated.Max(),
            entries.Count);
    }
}
