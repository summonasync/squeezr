using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class QueryTelemetrySummaryTests
{
    [Fact]
    public void Summarize_SearchAndRepeatedExpandOfSameFile_CountsFileOnceInCounterfactual()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", sizeBytes: 4000);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry(tool: "search", tokensUsed: 50),
            Entry(tool: "expand", tokensUsed: 100, file: "src/Big.cs"),
            Entry(tool: "expand", tokensUsed: 120, file: JsonSerializer.SerializeToElement(new { file = "src/Big.cs" }))
        };

        var summary = QueryTelemetrySummary.Summarize(entries, root);

        var fullFileTokens = Math.Max(1, (int)Math.Ceiling(4000 / 4.0 * 1.1));
        summary.Savings.CounterfactualTokens.ShouldBe(fullFileTokens);
        summary.Savings.ActualTokens.ShouldBe(270);
        summary.Savings.SavedTokens.ShouldBe(fullFileTokens - 270);
        summary.Savings.Ratio.ShouldBe(fullFileTokens / 270.0, tolerance: 0.0001);
        summary.Savings.UnknownFiles.ShouldBe(0);
        summary.TotalRequests.ShouldBe(3);
        summary.Records.ShouldBe(3);

        var expand = summary.ByTool.Single(t => t.Tool == "expand");
        expand.Count.ShouldBe(2);
        expand.AvgTokensUsed.ShouldBe(110);
        expand.TruncationRate.ShouldBe(0);
        summary.ByTool.Single(t => t.Tool == "search").Count.ShouldBe(1);
    }

    [Fact]
    public void Summarize_ExpandOfFileMissingFromIndex_ExcludedAndCountedUnknown()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", sizeBytes: 4000);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry(tool: "expand", tokensUsed: 30, file: "src/Gone.cs"),
            Entry(tool: "search", tokensUsed: 10)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, root);

        summary.Savings.CounterfactualTokens.ShouldBe(0);
        summary.Savings.UnknownFiles.ShouldBe(1);
        summary.Savings.ActualTokens.ShouldBe(40);
        summary.Savings.SavedTokens.ShouldBe(0);
        summary.Savings.Ratio.ShouldBe(0);
    }

    [Fact]
    public void Summarize_EmptyInput_AllZerosAndNullPeriod()
    {
        var summary = QueryTelemetrySummary.Summarize([], TestDirs.NewTempDir());

        summary.TotalRequests.ShouldBe(0);
        summary.Records.ShouldBe(0);
        summary.ByTool.ShouldBeEmpty();
        summary.Savings.ActualTokens.ShouldBe(0);
        summary.Savings.CounterfactualTokens.ShouldBe(0);
        summary.Savings.SavedTokens.ShouldBe(0);
        summary.Savings.Ratio.ShouldBe(0);
        summary.Savings.UnknownFiles.ShouldBe(0);
        summary.Cache.Hits.ShouldBe(0);
        summary.Cache.Misses.ShouldBe(0);
        summary.Cache.HitRate.ShouldBe(0);
        summary.Cache.SessionsWithCache.ShouldBe(0);
        summary.From.ShouldBeNull();
        summary.To.ShouldBeNull();
    }

    [Fact]
    public void Summarize_CacheHitEntries_HitRateAndDistinctSessions()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 10, cacheHit: true, sessionId: sessionA),
            Entry("code_search", 10, cacheHit: true, sessionId: sessionA),
            Entry("symbol_lookup", 10, cacheHit: true, sessionId: sessionB),
            Entry("repo_map", 10, cacheHit: false, sessionId: sessionB)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        summary.Cache.Hits.ShouldBe(3);
        summary.Cache.Misses.ShouldBe(1);
        summary.Cache.HitRate.ShouldBe(0.75, tolerance: 0.0001);
        summary.Cache.SessionsWithCache.ShouldBe(2);
    }

    [Fact]
    public void Summarize_TruncationRateAndPeriod_AggregatedAcrossEntries()
    {
        var from = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 17, 9, 30, 0, DateTimeKind.Utc);
        var entries = new List<QueryTelemetryEntry>
        {
            new(from, Guid.NewGuid(), "search", null, 100, true, 3, 5, false, null),
            new(to, Guid.NewGuid(), "search", null, 200, false, 1, 6, false, null)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        var search = summary.ByTool.Single();
        search.TruncationRate.ShouldBe(0.5, tolerance: 0.0001);
        search.AvgTokensUsed.ShouldBe(150);
        summary.From.ShouldBe(from);
        summary.To.ShouldBe(to);
    }

    private static string NewWorkspaceWithIndexedFile(string path, long sizeBytes)
    {
        var root = TestDirs.NewTempDir();
        var storeDir = Path.Combine(root, ".squeezr");
        Directory.CreateDirectory(storeDir);
        using var store = new MetaStore(Path.Combine(storeDir, "meta.db"), createIfMissing: true);
        store.AddRepo(new RepoDocument { Id = 1, Name = "demo", Path = "repo" });
        store.UpsertFile(new FileDocument { Id = 1, RepoId = 1, Path = path, SizeBytes = sizeBytes });
        return root;
    }

    private static QueryTelemetryEntry Entry(
        string tool,
        int tokensUsed,
        object? file = null,
        bool cacheHit = false,
        Guid? sessionId = null) =>
        new(
            DateTime.UtcNow, sessionId ?? Guid.NewGuid(), tool,
            file is null ? null : new { file },
            tokensUsed, false, 0, 5, cacheHit, null);
}
