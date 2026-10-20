using System.Text.RegularExpressions;
using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

public sealed class StatsRenderTests
{
    private const long BigFileBytes = 4000;
    private static readonly long WholeFileBaseline = Math.Max(1, (int)Math.Ceiling(BigFileBytes / 4.0 * 1.1));

    [Fact]
    public void Render_ExpandHeavyBaselineExceedsActual_ShowsQueryCostBaselineSavedRatio()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("expand", 100, file: "src/Big.cs"),
            Entry("search", 50)
        };

        var output = Render(entries, root);

        WholeFileBaseline.ShouldBe(1100);
        output.ShouldContain("Savings: query cost 150");
        output.ShouldContain($"whole-file baseline {WholeFileBaseline}");
        output.ShouldContain($"saved {WholeFileBaseline - 150}");
        output.ShouldContain("(×");
        output.ShouldNotContain("no whole-file baseline");
        output.ShouldNotContain("actual vs");
    }

    [Fact]
    public void Render_SearchOnlyWithoutExpands_PrintsNoBaselineAndNoSavedOrRatio()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("search", 120),
            Entry("code_search", 80)
        };

        var output = Render(entries, root);

        output.ShouldContain("Savings: no whole-file baseline for this usage mix");
        output.ShouldContain("query cost 200");
        output.ShouldNotContain("saved");
        output.ShouldNotContain("(×");
        output.ShouldNotContain("actual vs");
    }

    [Fact]
    public void Render_SearchHeavyLowExpand_BaselineBelowActual_KillsSavedZeroShape()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("expand", 100, file: "src/Big.cs"),
            Entry("search", 5000),
            Entry("code_search", 3000)
        };

        var output = Render(entries, root);

        output.ShouldContain("Savings: no whole-file baseline for this usage mix");
        output.ShouldContain("query cost 8100");
        output.ShouldNotContain("saved 0");
        output.ShouldNotContain("(×0");
        output.ShouldNotContain("actual vs");
        output.ShouldNotContain("counterfactual tokens");
    }

    [Fact]
    public void Render_NoCacheHitsWithLiveSessions_DropsTheZeroSessionContradiction()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var session = Guid.NewGuid();
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 120, sessionId: session),
            Entry("code_search", 90, sessionId: session),
            Entry("expand", 100, file: "src/Big.cs", sessionId: session)
        };

        var output = Render(entries, root);

        output.ShouldContain("Cache hit-rate: no hits yet across 1 recorded session(s)");
        output.ShouldNotContain("across 0 session(s)");
        output.ShouldNotContain("Cache hit-rate: 0.0 %");
    }

    [Fact]
    public void Render_NoCacheHitsAfterInvalidations_NamesTheIndexChange()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var session = Guid.NewGuid();
        var invalidated = new DateTime(2026, 9, 27, 14, 3, 0, DateTimeKind.Utc);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 120, sessionId: session, ts: invalidated.AddMinutes(-5)),
            Entry("code_search", 90, sessionId: session, ts: invalidated, cacheInvalidated: true),
            Entry("code_search", 95, sessionId: session, ts: invalidated.AddMinutes(2), cacheInvalidated: true)
        };

        var output = Render(entries, root);

        output.ShouldContain("Cache hit-rate: no hits yet across 1 recorded session(s)");
        output.ShouldContain("the index changed 2 time(s), last at 2026-09-27 14:05");
    }

    [Fact]
    public void Render_WithCacheHits_KeepsTheRateAndSessionCount()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var session = Guid.NewGuid();
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 120, sessionId: session),
            Entry("code_search", 120, sessionId: session, cacheHit: true)
        };

        var output = Render(entries, root);

        output.ShouldContain("Cache hit-rate: 50.0 % across 1 session(s)");
        output.ShouldNotContain("no hits yet");
    }

    [Fact]
    public void Render_MixedCliAndServeWorkload_RateNamesTheEligibleDenominator()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var session = Guid.NewGuid();
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("code_search", 120, sessionId: session),
            Entry("code_search", 90, sessionId: session, cacheHit: true),
            Entry("expand", 100, file: "src/Big.cs", sessionId: session),
            Entry("search", 40, sessionId: session)
        };

        var output = Render(entries, root);

        output.ShouldContain("Cache hit-rate: 50.0 % across 1 session(s) over 2 cache-eligible queries");
        output.ShouldNotContain("no cache-eligible queries yet");
    }

    [Fact]
    public void Render_CliOnlyStore_SaysThereIsNothingBehindTheServeCache()
    {
        var root = NewWorkspaceWithIndexedFile("src/Big.cs", BigFileBytes);
        var entries = new List<QueryTelemetryEntry>
        {
            Entry("search", 40),
            Entry("expand", 100, file: "src/Big.cs")
        };

        var output = Render(entries, root);

        output.ShouldContain("Cache hit-rate: no cache-eligible queries yet");
        output.ShouldNotContain("no hits yet");
        output.ShouldNotContain("Cache hit-rate: 0.0 %");
    }

    private static string Render(IReadOnlyList<QueryTelemetryEntry> entries, string root)
    {
        var summary = QueryTelemetrySummary.Summarize(entries, root);
        var console = new TestConsole();
        console.Profile.Width = 200;
        StatsCommand.Render(console, summary);
        return Regex.Replace(console.Output, @"\s+", " ");
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
        string? file = null,
        bool cacheHit = false,
        bool cacheInvalidated = false,
        Guid? sessionId = null,
        DateTime? ts = null) =>
        new(
            ts ?? DateTime.UtcNow, sessionId ?? Guid.NewGuid(), tool,
            file is null ? null : new { file },
            tokensUsed, false, 0, 5, cacheHit, null,
            CacheInvalidated: cacheInvalidated);
}
