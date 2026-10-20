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
        summary.Savings.BaselineLabel.ShouldBe(QueryTelemetrySummary.WholeFileBaselineLabel);
        summary.Savings.BaselineApplies.ShouldBeTrue();
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
        summary.Savings.BaselineApplies.ShouldBeFalse();
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
        summary.Savings.BaselineLabel.ShouldBe(QueryTelemetrySummary.WholeFileBaselineLabel);
        summary.Savings.BaselineApplies.ShouldBeFalse();
        summary.Cache.Hits.ShouldBe(0);
        summary.Cache.Misses.ShouldBe(0);
        summary.Cache.HitRate.ShouldBe(0);
        summary.Cache.SessionsWithCache.ShouldBe(0);
        summary.Cache.Invalidations.ShouldBe(0);
        summary.Cache.LastInvalidation.ShouldBeNull();
        summary.From.ShouldBeNull();
        summary.To.ShouldBeNull();
        summary.Agents.ShouldBeEmpty();
        summary.Sessions.ShouldBeEmpty();
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
        summary.Cache.Invalidations.ShouldBe(0);
        summary.Cache.LastInvalidation.ShouldBeNull();
        summary.Cache.EligibleQueries.ShouldBe(4);
    }

    [Fact]
    public void Summarize_MixedCliAndServeRows_DenominatorCountsOnlyCacheEligibleTools()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 10, cacheHit: true, sessionId: sessionA),
            Entry("code_search", 10, cacheHit: false, sessionId: sessionA),
            Entry("symbol_lookup", 10, cacheHit: true, sessionId: sessionB),
            Entry("search", 10, cacheHit: false, sessionId: Guid.NewGuid()),
            Entry("expand_span", 10, cacheHit: false, sessionId: Guid.NewGuid()),
            Entry("index_status", 10, cacheHit: false, sessionId: Guid.NewGuid())
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        summary.TotalRequests.ShouldBe(6);
        summary.Cache.EligibleQueries.ShouldBe(3);
        summary.Cache.Hits.ShouldBe(2);
        summary.Cache.Misses.ShouldBe(1);
        summary.Cache.HitRate.ShouldBe(2 / 3.0, tolerance: 0.0001);
        summary.Cache.SessionsWithCache.ShouldBe(2);
    }

    [Fact]
    public void Summarize_CliOnlyRows_CacheArithmeticStaysEmpty()
    {
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("search", 10),
            Entry("map", 10),
            Entry("expand", 10, file: "src/Big.cs")
        };

        var root = NewWorkspaceWithIndexedFile("src/Big.cs", sizeBytes: 4000);
        var summary = QueryTelemetrySummary.Summarize(entries, root);

        summary.TotalRequests.ShouldBe(3);
        summary.Cache.EligibleQueries.ShouldBe(0);
        summary.Cache.Hits.ShouldBe(0);
        summary.Cache.Misses.ShouldBe(0);
        summary.Cache.HitRate.ShouldBe(0);
        summary.Cache.SessionsWithCache.ShouldBe(0);
    }

    [Fact]
    public void Summarize_CacheInvalidatedEntries_CountsThemAndKeepsTheLatestTimestamp()
    {
        var session = Guid.NewGuid();
        var first = new DateTime(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);
        var latest = new DateTime(2026, 9, 27, 11, 45, 0, DateTimeKind.Utc);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 10, sessionId: session, ts: first, cacheInvalidated: true),
            Entry("code_search", 10, sessionId: session, ts: latest.AddMinutes(-30)),
            Entry("repo_map", 10, sessionId: session, ts: latest, cacheInvalidated: true),
            Entry("code_search", 10, sessionId: session, ts: latest, cacheHit: true)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        summary.Cache.Invalidations.ShouldBe(2);
        summary.Cache.LastInvalidation.ShouldBe(latest);
        summary.Cache.Hits.ShouldBe(1);
        summary.Cache.SessionsWithCache.ShouldBe(1);
        summary.TotalRequests.ShouldBe(4);
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

    [Fact]
    public void Summarize_MixedClients_GroupsByClientAndVersionSortedByCount()
    {
        var t1 = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 9, 24, 10, 5, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 9, 24, 10, 10, 0, DateTimeKind.Utc);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("search", 10, ts: t1, client: "kimi-code", clientVersion: "1.0"),
            Entry("code_search", 20, ts: t2, client: "kimi-code", clientVersion: "1.0"),
            Entry("search", 10, ts: t3, client: "cli"),
            Entry("expand", 10, ts: t1)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        summary.Agents.Count.ShouldBe(3);
        var kimi = summary.Agents[0];
        kimi.Client.ShouldBe("kimi-code");
        kimi.ClientVersion.ShouldBe("1.0");
        kimi.Count.ShouldBe(2);
        kimi.LastSeen.ShouldBe(t2);
        summary.Agents[1].Client.ShouldBe("cli");
        summary.Agents[1].ClientVersion.ShouldBeNull();
        summary.Agents[1].Count.ShouldBe(1);
        summary.Agents[1].LastSeen.ShouldBe(t3);
        summary.Agents[2].Client.ShouldBe("unknown");
        summary.Agents[2].ClientVersion.ShouldBeNull();
        summary.Agents[2].Count.ShouldBe(1);
        summary.Agents[2].LastSeen.ShouldBe(t1);
    }

    [Fact]
    public void Summarize_TwoSessions_ShortIdClientAndTopToolsSortedByLastSeen()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var t1 = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 9, 24, 10, 5, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 9, 24, 10, 10, 0, DateTimeKind.Utc);
        var t4 = new DateTime(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("search", 10, sessionId: sessionA, ts: t1, client: "kimi-code", clientVersion: "1.0"),
            Entry("search", 10, sessionId: sessionA, ts: t2, client: "kimi-code", clientVersion: "1.0"),
            Entry("code_search", 10, sessionId: sessionA, ts: t2, client: "kimi-code", clientVersion: "1.0"),
            Entry("expand", 10, sessionId: sessionA, ts: t3, client: "kimi-code", clientVersion: "1.0"),
            Entry("expand", 10, sessionId: sessionB, ts: t4)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        summary.Sessions.Count.ShouldBe(2);
        var legacy = summary.Sessions[0];
        legacy.SessionId.ShouldBe(sessionB.ToString("N")[..8]);
        legacy.Client.ShouldBe("unknown");
        legacy.Count.ShouldBe(1);
        legacy.LastSeen.ShouldBe(t4);
        legacy.TopTools.ShouldBe(["expand"]);
        var kimi = summary.Sessions[1];
        kimi.SessionId.ShouldBe(sessionA.ToString("N")[..8]);
        kimi.Client.ShouldBe("kimi-code");
        kimi.Count.ShouldBe(4);
        kimi.LastSeen.ShouldBe(t3);
        kimi.TopTools.ShouldBe(["search", "code_search", "expand"]);
    }

    [Fact]
    public void Summarize_SessionWithMixedClients_ClientComesFromLastSeenRecord()
    {
        var session = Guid.NewGuid();
        var t1 = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 9, 24, 10, 30, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("search", 10, sessionId: session, ts: t1, client: "cli"),
            Entry("code_search", 10, sessionId: session, ts: t2, client: "kimi-code", clientVersion: "1.0"),
            Entry("repo_map", 10, sessionId: session, ts: t3)
        };

        var summary = QueryTelemetrySummary.Summarize(entries, TestDirs.NewTempDir());

        var stats = summary.Sessions.Single();
        stats.SessionId.ShouldBe(session.ToString("N")[..8]);
        stats.Client.ShouldBe("unknown");
        stats.Count.ShouldBe(3);
        stats.LastSeen.ShouldBe(t3);
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
        Guid? sessionId = null,
        DateTime? ts = null,
        string? client = null,
        string? clientVersion = null,
        bool cacheInvalidated = false) =>
        new(
            ts ?? DateTime.UtcNow, sessionId ?? Guid.NewGuid(), tool,
            file is null ? null : new { file },
            tokensUsed, false, 0, 5, cacheHit, null, client, clientVersion, cacheInvalidated);
}
