using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class QueryTelemetryTests
{
    [Fact]
    public void ReadAll_AfterAppend_RoundTripsAllFields()
    {
        var root = TestDirs.NewTempDir();
        var entry = new QueryTelemetryEntry(
            Ts: new DateTime(2026, 9, 17, 12, 30, 5, DateTimeKind.Utc),
            SessionId: Guid.Parse("8f6f1c2e-0d4a-4b1e-9c3d-7a5e6f8a9b0c"),
            Tool: "search",
            Params: new { topK = 5, filter = "cs" },
            TokensUsed: 1234,
            Truncated: true,
            Hits: 42,
            DurationMs: 987_654,
            CacheHit: false,
            IndexAgeSeconds: 3600L);

        QueryTelemetry.Append(root, entry);

        var read = QueryTelemetry.ReadAll(root).Single();
        read.Ts.ShouldBe(entry.Ts);
        read.SessionId.ShouldBe(entry.SessionId);
        read.Tool.ShouldBe("search");
        var pruned = (JsonElement)read.Params!;
        pruned.GetProperty("topK").GetInt32().ShouldBe(5);
        pruned.GetProperty("filter").GetString().ShouldBe("cs");
        read.TokensUsed.ShouldBe(1234);
        read.Truncated.ShouldBeTrue();
        read.Hits.ShouldBe(42);
        read.DurationMs.ShouldBe(987_654L);
        read.CacheHit.ShouldBeFalse();
        read.IndexAgeSeconds.ShouldBe(3600L);
    }

    [Fact]
    public void ReadAll_AfterAppend_PreservesNullParamsAndIndexAge()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Append(root, Entry(tool: "status"));

        var read = QueryTelemetry.ReadAll(root).Single();
        read.Params.ShouldBeNull();
        read.IndexAgeSeconds.ShouldBeNull();
    }

    [Fact]
    public void ReadAll_CorruptedMiddleLine_SkipsItAndKeepsNeighbors()
    {
        var root = TestDirs.NewTempDir();
        QueryTelemetry.Append(root, Entry(tool: "search"));
        QueryTelemetry.Append(root, Entry(tool: "stats"));
        var path = SqueezrPaths.QueriesLogPath(root);
        var lines = File.ReadAllLines(path).ToList();
        lines.Insert(1, "{ this is :: not valid json");
        File.WriteAllLines(path, lines);

        var tools = QueryTelemetry.ReadAll(root).Select(e => e.Tool).ToList();

        tools.ShouldBe(["search", "stats"]);
    }

    [Fact]
    public void MissingQueriesLog_ReadAllAndCount_ReturnEmptyWithoutThrowing()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.ReadAll(root).ShouldBeEmpty();
        QueryTelemetry.Count(root).ShouldBe(0);
    }

    [Fact]
    public void Append_LogPathOccupiedByDirectory_DoesNotThrow()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.QueriesLogPath(root));

        Should.NotThrow(() => QueryTelemetry.Append(root, Entry(tool: "search")));
    }

    [Fact]
    public void Append_MultipleEntries_KeepsOrderAndTrailingNewline()
    {
        var root = TestDirs.NewTempDir();
        QueryTelemetry.Append(root, Entry(tool: "search"));
        QueryTelemetry.Append(root, Entry(tool: "symbols"));
        QueryTelemetry.Append(root, Entry(tool: "status"));

        var tools = QueryTelemetry.ReadAll(root).Select(e => e.Tool).ToList();
        tools.ShouldBe(["search", "symbols", "status"]);
        QueryTelemetry.Count(root).ShouldBe(3);
        File.ReadAllText(SqueezrPaths.QueriesLogPath(root)).ShouldEndWith("\n");
    }

    [Fact]
    public void ReadAll_LegacyLineWithoutClientFields_DeserializesWithNullClient()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        var legacyLine = "{\"ts\":\"2026-09-20T10:00:00Z\",\"sessionId\":\"8f6f1c2e-0d4a-4b1e-9c3d-7a5e6f8a9b0c\",\"tool\":\"search\",\"params\":null,\"tokensUsed\":100,\"truncated\":false,\"hits\":3,\"durationMs\":12,\"cacheHit\":false,\"indexAgeSeconds\":null}";
        File.WriteAllText(SqueezrPaths.QueriesLogPath(root), legacyLine + "\n");

        var read = QueryTelemetry.ReadAll(root).Single();

        read.Tool.ShouldBe("search");
        read.Client.ShouldBeNull();
        read.ClientVersion.ShouldBeNull();
        read.CacheInvalidated.ShouldBeFalse();
    }

    [Fact]
    public void ReadAll_AfterAppend_RoundTripsClientAndVersion()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Append(root, Entry(tool: "search") with { Client = "kimi-code", ClientVersion = "1.0.0" });

        var read = QueryTelemetry.ReadAll(root).Single();
        read.Client.ShouldBe("kimi-code");
        read.ClientVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public void Record_WithoutClientArgs_WritesCliClientAndOmitsClientVersion()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "search", null, 10, false, 1, 5);

        var raw = File.ReadAllText(SqueezrPaths.QueriesLogPath(root));
        raw.ShouldContain("\"client\":\"cli\"");
        raw.ShouldNotContain("clientVersion");
    }

    [Fact]
    public void Record_WithoutInvalidation_KeepsCacheInvalidatedOutOfTheLog()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "code_search", null, 10, false, 1, 5);

        File.ReadAllText(SqueezrPaths.QueriesLogPath(root)).ShouldNotContain("cacheInvalidated");
        QueryTelemetry.ReadAll(root).Single().CacheInvalidated.ShouldBeFalse();
    }

    [Fact]
    public void Record_WithInvalidation_RoundTripsTheFlag()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "code_search", null, 10, false, 1, 5, cacheInvalidated: true);

        File.ReadAllText(SqueezrPaths.QueriesLogPath(root)).ShouldContain("\"cacheInvalidated\":true");
        QueryTelemetry.ReadAll(root).Single().CacheInvalidated.ShouldBeTrue();
    }

    [Fact]
    public void Record_MappedAnswer_WritesTruncatedFalseMappedTrueLastInTheLine()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "symbol_lookup", null, 164, false, 1, 5, mapped: true);

        var raw = File.ReadAllText(SqueezrPaths.QueriesLogPath(root));
        raw.ShouldContain("\"truncated\":false");
        raw.ShouldContain("\"mapped\":true");
        Keys(raw).ShouldBe(
            "ts,sessionId,tool,params,tokensUsed,truncated,hits,durationMs,cacheHit,indexAgeSeconds,client,mapped");
        QueryTelemetry.ReadAll(root).Single().Mapped.ShouldBe(true);
    }

    [Fact]
    public void Record_ClippedAnswer_WritesTruncatedTrueMappedFalse()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "symbol_lookup", null, 40, true, 1, 5, mapped: false);

        var raw = File.ReadAllText(SqueezrPaths.QueriesLogPath(root));
        raw.ShouldContain("\"truncated\":true");
        raw.ShouldContain("\"mapped\":false");
        raw.ShouldEndWith(",\"mapped\":false}\n");
        QueryTelemetry.ReadAll(root).Single().Mapped.ShouldBe(false);
    }

    [Fact]
    public void Record_FullyPopulatedEntry_WritesMappedLastRightAfterBaselineTokens()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "symbol_lookup", new { name = "Huge" }, 85, false, 1, 6,
            cacheHit: true, indexAge: TimeSpan.FromSeconds(12), client: "qwen", clientVersion: "1.2.3",
            cacheInvalidated: true, baselineTokens: 1100, mapped: true);

        var raw = File.ReadAllText(SqueezrPaths.QueriesLogPath(root));
        Keys(raw).ShouldBe(
            "ts,sessionId,tool,params,tokensUsed,truncated,hits,durationMs,cacheHit,indexAgeSeconds," +
            "client,clientVersion,cacheInvalidated,baselineTokens,mapped");
        raw.ShouldEndWith("\"baselineTokens\":1100,\"mapped\":true}\n");
    }

    [Fact]
    public void Record_WithoutState_OmitsTheMappedKeySoTheRowStaysUnsplittable()
    {
        var root = TestDirs.NewTempDir();

        QueryTelemetry.Record(root, Guid.NewGuid(), "pre-read", null, 0, false, 0, 0, mapped: null);

        var raw = File.ReadAllText(SqueezrPaths.QueriesLogPath(root));
        raw.ShouldNotContain("mapped");
        QueryTelemetry.ReadAll(root).Single().Mapped.ShouldBeNull();
    }

    [Fact]
    public void ReadAll_PreSplitLine_StillParsesAndReadsAsNullMapped_WhileMappedFalseRowReadsAsFalse()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        const string preSplit = """{"ts":"2026-10-01T10:00:00Z","sessionId":"00000000-0000-0000-0000-000000000000","tool":"symbol_lookup","params":null,"tokensUsed":164,"truncated":true,"hits":1,"durationMs":5,"cacheHit":false,"indexAgeSeconds":null,"client":"cli"}""";
        File.WriteAllText(SqueezrPaths.QueriesLogPath(root), preSplit + '\n');
        QueryTelemetry.Record(root, Guid.NewGuid(), "symbol_lookup", null, 164, false, 1, 5, mapped: false);

        var rows = QueryTelemetry.ReadAll(root);

        rows.Count.ShouldBe(2);
        rows[0].Mapped.ShouldBeNull();
        rows[0].Truncated.ShouldBeTrue();
        rows[1].Mapped.ShouldBe(false);
        rows[1].Truncated.ShouldBeFalse();
    }

    [Fact]
    public void ReadAll_SymbolLookupRowFromBeforeTheBudgetKey_StillParsesWithoutIt()
    {
        // 115/T7 put the resolved ceiling into symbol_lookup's recorded params: an additive key, so a row
        // written by an older build has to keep loading with the key simply absent instead of failing the line
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        const string beforeBudgetKey = """{"ts":"2026-11-01T10:00:00Z","sessionId":"00000000-0000-0000-0000-000000000000","tool":"symbol_lookup","params":{"name":"Huge","kind":null,"repo":null,"secretGuardExtra":[]},"tokensUsed":36,"truncated":true,"hits":1,"durationMs":5,"cacheHit":false,"indexAgeSeconds":null,"client":"cli","mapped":false}""";
        File.WriteAllText(SqueezrPaths.QueriesLogPath(root), beforeBudgetKey + '\n');

        QueryTelemetry.Record(root, Guid.NewGuid(), "symbol_lookup",
            new { name = "Huge", kind = (string?)null, repo = (string?)null, maxTokens = 600, secretGuardExtra = Array.Empty<string>() },
            237, false, 1, 6, mapped: false);

        var rows = QueryTelemetry.ReadAll(root);

        rows.Count.ShouldBe(2);
        var legacyParams = (JsonElement)rows[0].Params!;
        legacyParams.TryGetProperty("maxTokens", out _).ShouldBeFalse();
        legacyParams.GetProperty("name").GetString().ShouldBe("Huge");
        ((JsonElement)rows[1].Params!).GetProperty("maxTokens").GetInt32().ShouldBe(600);
    }

    private static string Keys(string line) =>
        string.Join(',', JsonDocument.Parse(line).RootElement.EnumerateObject().Select(p => p.Name));

    private static QueryTelemetryEntry Entry(string tool) =>
        new(DateTime.UtcNow, Guid.NewGuid(), tool, null, 0, false, 0, 1, false, null);
}
