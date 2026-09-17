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

    private static QueryTelemetryEntry Entry(string tool) =>
        new(DateTime.UtcNow, Guid.NewGuid(), tool, null, 0, false, 0, 1, false, null);
}
