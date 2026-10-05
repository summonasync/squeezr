using System.Text.Json;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public sealed record ToolStats(string Tool, int Count, double AvgTokensUsed, double TruncationRate);

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
    DateTime? LastInvalidation = null);

public sealed record AgentStats(string Client, string? ClientVersion, int Count, DateTime LastSeen);

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
    IReadOnlyList<SessionStats> Sessions);

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
                g.Average(e => e.Truncated ? 1.0 : 0.0)))
            .OrderBy(t => t.Tool, StringComparer.Ordinal)
            .ToList();

        var (counterfactual, unknownFiles) = EstimateCounterfactual(entries, workspaceRoot);
        var actual = entries.Sum(e => (long)e.TokensUsed);
        var saved = Math.Max(0, counterfactual - actual);
        var ratio = counterfactual == 0 || actual == 0 ? 0 : counterfactual / (double)actual;
        var baselineApplies = counterfactual > actual;

        var hits = entries.Count(e => e.CacheHit);
        var hitRate = entries.Count == 0 ? 0 : hits / (double)entries.Count;

        return new QueryStatsSummary(
            entries.Count,
            byTool,
            new SavingsStats(actual, counterfactual, saved, ratio, unknownFiles, WholeFileBaselineLabel, baselineApplies),
            SummarizeCache(entries, hits, hitRate),
            entries.Count == 0 ? null : entries.Min(e => e.Ts),
            entries.Count == 0 ? null : entries.Max(e => e.Ts),
            entries.Count,
            AggregateAgents(entries),
            AggregateSessions(entries));
    }

    public static IReadOnlyList<AgentStats> AggregateAgents(IReadOnlyList<QueryTelemetryEntry> entries) =>
        entries
            .GroupBy(e => (Client: e.Client ?? "unknown", Version: e.ClientVersion))
            .Select(g => new AgentStats(g.Key.Client, g.Key.Version, g.Count(), g.Max(e => e.Ts)))
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

    private static (long Counterfactual, int UnknownFiles) EstimateCounterfactual(
        IReadOnlyList<QueryTelemetryEntry> entries, string workspaceRoot)
    {
        using var store = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(workspaceRoot));
        var indexedFiles = store?.ListAllFiles();
        var guard = SecretGuardReader.Load(workspaceRoot);
        var recordedPaths = RecordedPathShapes.Load(workspaceRoot);

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
            if (recordedPaths.IsProtected(guard, file))
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

    // A ledger row keeps the shape the caller typed, masks are authored against a repo root, and between those
    // sit four shapes of the same file: repo-qualified ('demo/infra/VaultKeys.cs', what the ambiguity hint tells
    // agents to write), workspace-relative with any number of segments ('repos/demo/infra/VaultKeys.cs'), absolute,
    // and any of those with a '..' in the middle. So every typed shape is resolved against every root and then read
    // back relative to every root — that is what collapses '..' and multi-segment prefixes at once. Asking the raw
    // string alone is a silent fail-open, and for a row 064/T4 already purged there is no stored row left for the
    // belt below to catch. Reading a workspace-relative path through a repo-root mask can over-exclude a plain file
    // from an advisory savings number; it can never name or serve anything.
    private sealed class RecordedPathShapes
    {
        private readonly string _workspaceRoot;
        private readonly IReadOnlyList<RepoShape> _repos;

        private RecordedPathShapes(string workspaceRoot, IReadOnlyList<RepoShape> repos)
        {
            _workspaceRoot = workspaceRoot;
            _repos = repos;
        }

        internal static RecordedPathShapes Load(string workspaceRoot)
        {
            try
            {
                if (!WorkspaceManifestFile.Exists(workspaceRoot))
                {
                    return new RecordedPathShapes(workspaceRoot, []);
                }

                var manifest = WorkspaceManifestFile.Load(workspaceRoot);
                return new RecordedPathShapes(workspaceRoot,
                    [.. manifest.Repos.Select(repo => new RepoShape(
                        repo.Name, NavigationContext.ResolveRepoRoot(workspaceRoot, repo)))]);
            }
            catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
            {
                return new RecordedPathShapes(workspaceRoot, []);
            }
        }

        internal bool IsProtected(SecretGuardReader guard, string recorded) =>
            Shapes(recorded).Any(guard.IsProtected);

        private IEnumerable<string> Shapes(string recorded)
        {
            var typed = recorded.Replace('\\', '/');
            yield return typed;

            foreach (var repo in _repos)
            {
                if (WithoutFirstSegment(typed, repo.Name) is { } qualified)
                {
                    yield return qualified;
                }
            }

            foreach (var absolute in ResolvedForms(typed))
            {
                foreach (var root in Roots())
                {
                    if (RelativeTo(root, absolute) is { } relative)
                    {
                        yield return relative;
                    }
                }
            }
        }

        private List<string> ResolvedForms(string typed)
        {
            var resolved = new List<string>(_repos.Count + 1);
            foreach (var root in Roots())
            {
                try
                {
                    var absolute = Path.GetFullPath(typed, root);
                    if (!resolved.Contains(absolute, PathComparer))
                    {
                        resolved.Add(absolute);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
            }

            return resolved;
        }

        private string[] Roots() => [.. new[] { _workspaceRoot }, .. _repos.Select(repo => repo.Root)];

        private static string? WithoutFirstSegment(string typed, string prefix) =>
            typed.Length > prefix.Length + 1 && typed.StartsWith($"{prefix}/", StringComparison.Ordinal)
                ? typed[(prefix.Length + 1)..]
                : null;

        private static string? RelativeTo(string root, string absolute)
        {
            try
            {
                var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
                return relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)
                        ? null
                        : relative;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private sealed record RepoShape(string Name, string Root);
    }

    private static CacheStats SummarizeCache(IReadOnlyList<QueryTelemetryEntry> entries, int hits, double hitRate)
    {
        var invalidated = entries.Where(e => e.CacheInvalidated).Select(e => e.Ts).ToList();
        return new CacheStats(
            hits,
            entries.Count - hits,
            hitRate,
            DistinctCacheSessions(entries),
            invalidated.Count,
            invalidated.Count == 0 ? null : invalidated.Max());
    }
}
