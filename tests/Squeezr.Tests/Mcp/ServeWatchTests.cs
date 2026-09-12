using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public sealed class ServeWatchTests
{
    private const int StormFiles = 40;

    private static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EngineQuietPeriod = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task WatchFlag_ConfigDisabled_EngineIndexesEditsInBackground()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        await using var harness = await McpTestHarness.StartAsync(workspace, watch: true);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"flaghit7\"; }");

        await UntilAsync(EngineTimeout, async () => await HasHitAsync(harness, "flaghit7"));
    }

    [Fact]
    public async Task ConfigEnabled_NoFlag_EngineIndexesEditsInBackground()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: true);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"cfghit7\"; }");

        await UntilAsync(EngineTimeout, async () => await HasHitAsync(harness, "cfghit7"));
    }

    [Fact]
    public async Task NoFlag_NoConfigEnable_EngineStaysOff_EditsNotIndexed()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"offhit7\"; }");

        await Task.Delay(EngineQuietPeriod);
        (await HasHitAsync(harness, "offhit7")).ShouldBeFalse();
    }

    [Fact]
    public async Task ParallelToolCalls_DuringCatchUpPass_AllSucceedWithValidEnvelopes()
    {
        var workspace = NewStormWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace, watch: true);

        for (var i = 0; i < StormFiles; i++)
        {
            WriteFile(workspace, $"repo/storm/file{i:D2}.cs", $"class Storm{i} {{ string Term = \"zqxwv{i}\"; }}");
        }

        var calls = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("code_search", new() { ["query"] = $"zqxwv{StormFiles - 1}" }),
            ("index_status", []),
            ("find_references", new() { ["symbol"] = "Alpha" }),
            ("symbol_lookup", new() { ["name"] = "Alpha" }),
            ("repo_map", []),
            ("expand_span", new() { ["file"] = "a.cs", ["startLine"] = 1, ["endLine"] = 1 })
        };

        var batches = 0;
        var deadline = DateTime.UtcNow + EngineTimeout;
        while (DateTime.UtcNow < deadline && !await HasHitAsync(harness, $"zqxwv{StormFiles - 1}"))
        {
            var results = await Task.WhenAll(calls.Select(c =>
                harness.Client.CallToolAsync(c.Tool, c.Args, cancellationToken: CancellationToken.None).AsTask()));

            foreach (var ((tool, _), result) in calls.Zip(results))
            {
                result.IsError.ShouldNotBe(
                    true, $"tool {tool} failed during background pass: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
                ParseEnvelope(result).GetProperty("data");
            }

            batches++;
            await Task.Delay(100);
        }

        batches.ShouldBeGreaterThan(0, "no parallel batches ran before the pass finished");
        (await HasHitAsync(harness, $"zqxwv{StormFiles - 1}")).ShouldBeTrue(
            $"catch-up pass did not land within {EngineTimeout.TotalSeconds}s");
    }

    private static async Task<bool> HasHitAsync(McpTestHarness harness, string term)
    {
        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = term },
            cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(
            true, $"code_search '{term}' failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        return ParseEnvelope(result).GetProperty("data").EnumerateArray().Any();
    }

    private static async Task UntilAsync(TimeSpan timeout, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(200);
        }
        (await condition()).ShouldBeTrue($"condition not met within {timeout.TotalSeconds}s");
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewIndexedWorkspace(bool watchEnabled)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        ApplyWatchConfig(workspace, watchEnabled);
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { Alpha Make() { return new Alpha(); } }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string NewStormWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        ApplyWatchConfig(workspace, watchEnabled: false);
        for (var i = 0; i < StormFiles; i++)
        {
            WriteFile(workspace, $"repo/storm/file{i:D2}.cs", $"class Storm{i} {{ string Term = \"old{i}\"; }}");
        }
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { Alpha Make() { return new Alpha(); } }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void ApplyWatchConfig(string workspace, bool watchEnabled)
    {
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { Enabled = watchEnabled, DebounceSeconds = 1 }
        });
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
