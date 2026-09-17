using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public sealed class ToolResponseCacheTests
{
    [Fact]
    public void TryGet_AfterPutWithSameParameters_ReturnsSameInstance()
    {
        var cache = NewCache();
        var envelope = new ToolEnvelope(Array.Empty<object>(), 42, false, [], null, []);

        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), envelope);

        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out var cached).ShouldBeTrue();
        cached.ShouldBeSameAs(envelope);
    }

    [Fact]
    public void TryGet_DifferentParametersOrTool_Misses()
    {
        var cache = NewCache();
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "envelope");

        cache.TryGet("code_search", SearchParams("Sub", "demo", 2000, 5), out _).ShouldBeFalse();
        cache.TryGet("code_search", SearchParams("Add", "other", 2000, 5), out _).ShouldBeFalse();
        cache.TryGet("code_search", SearchParams("Add", "demo", 1000, 5), out _).ShouldBeFalse();
        cache.TryGet("repo_map", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGet_PropertyOrderIrrelevant_Hits()
    {
        var cache = NewCache();
        cache.Put("symbol_lookup", new { a = 1, b = 2 }, "envelope");

        cache.TryGet("symbol_lookup", new { b = 2, a = 1 }, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryGet_NullPropertyEquivalentToAbsentProperty_Hits()
    {
        var cache = NewCache();
        cache.Put("find_references", new { symbol = "Calc", repo = (string?)null }, "envelope");

        cache.TryGet("find_references", new { symbol = "Calc" }, out _).ShouldBeTrue();
        cache.Put("repo_map", new { repo = "demo" }, "map");
        cache.TryGet("repo_map", new { repo = "demo", depth = (int?)null }, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryGet_AfterMetaDbMtimeChange_Misses()
    {
        var root = TestDirs.NewTempDir();
        var metaDbPath = SqueezrPaths.MetaDbPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(metaDbPath)!);
        File.WriteAllText(metaDbPath, "v1");
        var cache = new ToolResponseCache(root);

        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "envelope");
        var before = cache.Generation;
        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));

        cache.Generation.ShouldNotBe(before);
        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
    }

    [Fact]
    public void Put_OverCapacity_EvictsOldestKeepsNewest()
    {
        var root = TestDirs.NewTempDir();
        var cache = new ToolResponseCache(root, capacity: 100);
        for (var i = 0; i <= 100; i++)
        {
            cache.Put("code_search", new { query = $"q{i}", repo = (string?)null, maxTokens = (int?)null, topK = (int?)null }, i);
        }

        cache.TryGet("code_search", new { query = "q0", repo = (string?)null, maxTokens = (int?)null, topK = (int?)null }, out _).ShouldBeFalse();
        cache.TryGet("code_search", new { query = "q100", repo = (string?)null, maxTokens = (int?)null, topK = (int?)null }, out var newest).ShouldBeTrue();
        newest.ShouldBe(100);
    }

    [Fact]
    public async Task McpIdenticalCodeSearchCalls_SecondTelemetryRecord_MarksCacheHit()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        for (var call = 0; call < 2; call++)
        {
            var result = await harness.Client.CallToolAsync(
                "code_search",
                new Dictionary<string, object?> { ["query"] = "Add" },
                cancellationToken: CancellationToken.None);
            result.IsError.ShouldNotBe(true);
        }

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e.Tool == "code_search");
        entries[0].CacheHit.ShouldBeFalse();
        entries[1].CacheHit.ShouldBeTrue();
    }

    private static ToolResponseCache NewCache() => new(TestDirs.NewTempDir());

    private static object SearchParams(string query, string? repo, int? maxTokens, int? topK) =>
        new { query, repo, maxTokens, topK };

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
