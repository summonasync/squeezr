using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class QueryTelemetryBaselineTests
{
    [Fact]
    public void RoundTrip_NewFieldParsesBack_LegacyLineStaysNull()
    {
        var entry = new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "code_search", null, 50, false, 3, 12, false, null,
            BaselineTokens: 900);

        var line = JsonSerializer.Serialize(entry);
        line.ShouldContain("\"baselineTokens\":900");
        JsonSerializer.Deserialize<QueryTelemetryEntry>(line)!.BaselineTokens.ShouldBe(900);

        const string legacy = """{"ts":"2026-09-01T10:00:00Z","sessionId":"00000000-0000-0000-0000-000000000000","tool":"expand_span","params":null,"tokensUsed":10,"truncated":false,"hits":1,"durationMs":5,"cacheHit":false,"indexAgeSeconds":null}""";
        JsonSerializer.Deserialize<QueryTelemetryEntry>(legacy)!.BaselineTokens.ShouldBeNull();
    }

    [Fact]
    public void Summarize_PrefersRecordedBaseline_OverFallbackEstimate()
    {
        var root = NewStoreRoot(out _);
        var entry = new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "expand_span", new { file = "src/Ghost.cs" }, 100, false, 1, 5, false,
            null, BaselineTokens: 5000);

        var summary = QueryTelemetrySummary.Summarize([entry], root);

        summary.Savings.CounterfactualTokens.ShouldBe(5000);
        summary.Savings.UnknownFiles.ShouldBe(0);
    }

    [Fact]
    public void Summarize_LegacyEntry_FallsBackToSharedEstimator()
    {
        var root = NewStoreRoot(out var store);
        var repo = InsertRepo(store, "alpha");
        InsertFile(store, repo.Id, "src/Big.cs", 4000);

        var entry = new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "expand_span", new { file = "src/Big.cs" }, 100, false, 1, 5, false, null);

        var summary = QueryTelemetrySummary.Summarize([entry], root);

        summary.Savings.CounterfactualTokens.ShouldBe(TokenEstimate.WholeFile(4000));
    }

    [Fact]
    public void Drift_EnvelopeBaseline_EqualsStatsBaseline_OnSameFixture()
    {
        var root = NewStoreRoot(out var store);
        var repo = InsertRepo(store, "alpha");
        InsertFile(store, repo.Id, "src/Big.cs", 4000);

        var (cost, _) = CostBaseline.Measure(
            store, [("alpha", "src/Big.cs")], tokensUsed: 100, complete: true);
        var entry = new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "expand_span", new { file = "src/Big.cs" }, 100, false, 1, 5, false,
            null, BaselineTokens: cost.BaselineTokens);

        var summary = QueryTelemetrySummary.Summarize([entry], root);

        cost.BaselineTokens.ShouldNotBeNull();
        summary.Savings.CounterfactualTokens.ShouldBe(cost.BaselineTokens!.Value);
    }

    private static string NewStoreRoot(out MetaStore store)
    {
        var root = TestDirs.NewTempDir();
        var storeDir = Path.Combine(root, ".squeezr");
        Directory.CreateDirectory(storeDir);
        store = new MetaStore(Path.Combine(storeDir, "meta.db"), createIfMissing: true);
        return root;
    }

    private static RepoDocument InsertRepo(MetaStore store, string name)
    {
        var repo = new RepoDocument { Id = store.GetNextRepoId(), Name = name };
        store.UpsertRepo(repo);
        return repo;
    }

    private static void InsertFile(MetaStore store, uint repoId, string path, long sizeBytes)
    {
        store.UpsertFile(new FileDocument
        {
            Id = store.GetNextFileId(),
            RepoId = repoId,
            Path = path,
            SizeBytes = sizeBytes,
        });
    }
}
