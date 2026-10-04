using System.Text.Json;
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
        var (cache, metaDbPath) = NewCacheWithMetaDb();

        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "envelope");
        var before = cache.Generation;
        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));

        cache.Generation.ShouldNotBe(before);
        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGet_AfterConfigMtimeChange_Misses()
    {
        // a config edit moves no meta.db byte, yet indexing.rankExclude and indexing.secretGuardExtra shape
        // the answer — without the config clock the entry computed under the older policy keeps being served
        var (cache, _, configPath) = NewCacheWithStoreFiles();

        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "envelope");
        var before = cache.Generation;

        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddSeconds(30));

        cache.Generation.ShouldNotBe(before);
        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
        cache.Count.ShouldBe(0);
        cache.TakeInvalidationWarning().ShouldBe(ToolResponseCache.InvalidationWarning);
    }

    [Fact]
    public void Generation_ConfigOlderThanIndex_StillMovesOnTheIndexWrite()
    {
        // max(index, config), not a sum or a pair: a workspace that has not had its config touched for
        // weeks must keep invalidating on every index pass
        var (cache, metaDbPath, configPath) = NewCacheWithStoreFiles();
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(-30));
        var before = cache.Generation;

        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow);

        cache.Generation.ShouldNotBe(before);
    }

    [Fact]
    public void Put_AfterIndexWriteDuringTheCall_StaleAnswerDoesNotSurviveTheNextRead()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        var parameters = SearchParams("Add", "demo", 2000, 5);

        cache.TryGet("code_search", parameters, out _).ShouldBeFalse();

        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));
        cache.Put("code_search", parameters, "answer computed before the write");

        cache.TryGet("code_search", parameters, out _).ShouldBeFalse(
            "a response read before an index write must not be filed under the generation that write produced");
        cache.Count.ShouldBe(0);
        cache.TakeInvalidationWarning().ShouldBe(ToolResponseCache.InvalidationWarning);
    }

    [Fact]
    public void Put_AfterASecondMissOnTheSameKey_StaleAnswerStillDoesNotSurvive()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        var parameters = SearchParams("Add", "demo", 2000, 5);

        cache.TryGet("code_search", parameters, out _).ShouldBeFalse();

        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));

        // A second miss must not vouch for the first caller's answer: without TryAdd this re-tags the
        // marker to the new generation and the stale Put below lands as if it were fresh.
        cache.TryGet("code_search", parameters, out _).ShouldBeFalse();
        cache.Put("code_search", parameters, "answer computed before the write");

        cache.TryGet("code_search", parameters, out _).ShouldBeFalse();
    }

    [Fact]
    public void Put_WhenTheGenerationMovedBackwards_DoesNotCache()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        var parameters = SearchParams("Add", "demo", 2000, 5);
        var readStamp = DateTime.UtcNow;

        File.SetLastWriteTimeUtc(metaDbPath, readStamp);
        cache.TryGet("code_search", parameters, out _).ShouldBeFalse();

        File.SetLastWriteTimeUtc(metaDbPath, readStamp.AddSeconds(-30));
        cache.Put("code_search", parameters, "answer from the rolled-back clock");

        // The next read's sweep would drop a wrongly-tagged entry anyway, so the real exposure is the clock
        // catching up: an answer of unknowable freshness must not be sitting there as a hit when it returns.
        File.SetLastWriteTimeUtc(metaDbPath, readStamp);

        cache.TryGet("code_search", parameters, out _).ShouldBeFalse(
            "an answer computed across a backwards clock step cannot be served as fresh when the stamp returns");
    }

    [Fact]
    public void Put_WithoutAnIndexWriteDuringTheCall_StillCaches()
    {
        var (cache, _) = NewCacheWithMetaDb();
        var parameters = SearchParams("Add", "demo", 2000, 5);

        cache.TryGet("code_search", parameters, out _).ShouldBeFalse();
        cache.Put("code_search", parameters, "fresh");

        cache.TryGet("code_search", parameters, out var cached).ShouldBeTrue();
        cached.ShouldBe("fresh");
    }

    [Fact]
    public void TryGet_AfterGenerationChange_DropsDeadEntriesAndArmsOneWarning()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "first");
        cache.Put("repo_map", MapParams("demo"), "second");
        cache.Count.ShouldBe(2);

        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));

        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
        cache.Count.ShouldBe(0);
        cache.TakeInvalidationWarning().ShouldBe(ToolResponseCache.InvalidationWarning);
        cache.TakeInvalidationWarning().ShouldBeNull();
    }

    [Fact]
    public void Put_SameKeyOnANewGeneration_HoldsOneEntryNotTwo()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "v1");

        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "v2");

        cache.Count.ShouldBe(1);
        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out var live).ShouldBeTrue();
        live.ShouldBe("v2");
    }

    [Fact]
    public void TakeInvalidationWarning_MetaDbDisappears_ArmsAsWell()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "first");

        File.Delete(metaDbPath);

        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
        cache.Count.ShouldBe(0);
        cache.TakeInvalidationWarning().ShouldBe(ToolResponseCache.InvalidationWarning);
    }

    [Fact]
    public void TakeInvalidationWarning_GenerationChangeOverAnEmptyCache_StaysQuiet()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();

        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));
        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();

        cache.TakeInvalidationWarning().ShouldBeNull();
    }

    [Fact]
    public void TakeInvalidationWarning_PlainMissOnALiveGeneration_StaysQuiet()
    {
        var (cache, _) = NewCacheWithMetaDb();
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "first");

        cache.TryGet("code_search", SearchParams("Sub", "demo", 2000, 5), out _).ShouldBeFalse();

        cache.TakeInvalidationWarning().ShouldBeNull();
        cache.Count.ShouldBe(1);
    }

    [Fact]
    public void TakeInvalidationWarning_NextGenerationChange_ArmsAgain()
    {
        var (cache, metaDbPath) = NewCacheWithMetaDb();
        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "first");
        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(30));
        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
        cache.TakeInvalidationWarning().ShouldNotBeNull();

        cache.Put("code_search", SearchParams("Add", "demo", 2000, 5), "second");
        File.SetLastWriteTimeUtc(metaDbPath, DateTime.UtcNow.AddSeconds(90));

        cache.TryGet("code_search", SearchParams("Add", "demo", 2000, 5), out _).ShouldBeFalse();
        cache.TakeInvalidationWarning().ShouldBe(ToolResponseCache.InvalidationWarning);
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

    [Fact]
    public async Task McpIdenticalCodeSearchAcrossAGenerationBump_CarriesTheMarkerOnce()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        Warnings(await SearchAsync(harness)).ShouldNotContain(ToolResponseCache.InvalidationWarning);

        File.SetLastWriteTimeUtc(SqueezrPaths.MetaDbPath(root), DateTime.UtcNow.AddSeconds(30));

        Warnings(await SearchAsync(harness)).ShouldContain(ToolResponseCache.InvalidationWarning);
        Warnings(await SearchAsync(harness)).ShouldNotContain(ToolResponseCache.InvalidationWarning);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(3);
        entries[0].CacheInvalidated.ShouldBeFalse();
        entries[1].CacheInvalidated.ShouldBeTrue();
        entries[2].CacheInvalidated.ShouldBeFalse();
        entries[2].CacheHit.ShouldBeTrue();
    }

    private static async Task<JsonElement> SearchAsync(McpTestHarness harness)
    {
        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
        return JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!).RootElement.Clone();
    }

    private static IReadOnlyList<string> Warnings(JsonElement envelope) =>
        envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    private static ToolResponseCache NewCache() => new(TestDirs.NewTempDir());

    private static (ToolResponseCache Cache, string MetaDbPath) NewCacheWithMetaDb()
    {
        var root = TestDirs.NewTempDir();
        var metaDbPath = SqueezrPaths.MetaDbPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(metaDbPath)!);
        File.WriteAllText(metaDbPath, "v1");
        return (new ToolResponseCache(root), metaDbPath);
    }

    private static (ToolResponseCache Cache, string MetaDbPath, string ConfigPath) NewCacheWithStoreFiles()
    {
        var root = TestDirs.NewTempDir();
        var metaDbPath = SqueezrPaths.MetaDbPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(metaDbPath)!);
        File.WriteAllText(metaDbPath, "v1");
        var configPath = SqueezrPaths.ConfigPath(root);
        File.WriteAllText(configPath, "{}");
        return (new ToolResponseCache(root), metaDbPath, configPath);
    }

    private static object SearchParams(string query, string? repo, int? maxTokens, int? topK) =>
        new { query, repo, maxTokens, topK };

    private static object MapParams(string repo) =>
        new { repo, depth = (int?)null, maxTokens = (int?)null };

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
