using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public sealed class ServeWatchClaimTests
{
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EngineQuietPeriod = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EngineArmTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ClaimHeldElsewhere_ServeWatch_ArmsNoEngine_KeepsServingQueries()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        var storeDir = SqueezrPaths.StoreDir(workspace);
        HoldClaim(storeDir);

        await using var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: ClaimAs(storeDir, ForeignPid()));

        await ServerAnswersAsync(harness);
        File.Exists(DaemonHeartbeat.PathFor(storeDir, DaemonHeartbeat.ServeWatchKind)).ShouldBeFalse(
            "serve armed a watch engine although the store claim is held elsewhere");
        ReadOwnerPid(storeDir).ShouldBe(
            Environment.ProcessId, "the non-owner serve replaced or reaped the existing claim");

        // a.cs keeps a term the old index matches, so a query on it routes the changed file through
        // FreshnessGuard; c.cs is reachable by no indexed term, so only a watch pass could surface it.
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; string Note = \"zqwvj7\"; }");
        WriteFile(workspace, "repo/c.cs", "class Gamma { string Term = \"zqxwatch9\"; }");
        await Task.Delay(EngineQuietPeriod);

        (await HasHitAsync(harness, "uniqneedle")).ShouldBeTrue("the non-owner lost the query path");
        (await HasHitAsync(harness, "zqwvj7")).ShouldBeTrue("query-path freshness did not run on the non-owner");
        (await HasHitAsync(harness, "zqxwatch9")).ShouldBeFalse(
            "a watch pass ran on the non-owner, which must never enter the index pass path");
        ReadOwnerPid(storeDir).ShouldBe(Environment.ProcessId, "the claim marker changed during the quiet period");
    }

    [Fact]
    public async Task TwoParticipants_OnOneStore_OwnerIndexes_ObserverAnswers()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        await using var owner = await McpTestHarness.StartAsync(workspace, watch: true);
        await WaitForEngineArmedAsync(workspace);
        await using var observer = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: ClaimAs(SqueezrPaths.StoreDir(workspace), ForeignPid()));

        var storeDir = SqueezrPaths.StoreDir(workspace);
        ReadOwnerPid(storeDir).ShouldBe(Environment.ProcessId, "the observer took the claim from the owner");
        DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.ServeWatchKind)!.Pid
            .ShouldBe(Environment.ProcessId, "two heartbeat writers appeared on one store");

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"twinhit9\"; }");
        await UntilAsync(EngineTimeout, async () => await HasHitAsync(owner, "twinhit9"));
        (await HasHitAsync(observer, "twinhit9")).ShouldBeTrue(
            "the observer answers from the shared store only after the owner's pass lands");
    }

    [Fact]
    public async Task QueryResults_OwnerAndObserver_IdenticalOnSameFixture()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        await using var owner = await McpTestHarness.StartAsync(workspace, watch: true);
        await WaitForEngineArmedAsync(workspace);
        await using var observer = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: ClaimAs(SqueezrPaths.StoreDir(workspace), ForeignPid()));

        var probes = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("code_search", new() { ["query"] = "uniqneedle" }),
            ("code_search", new() { ["query"] = "class Beta" }),
            ("symbol_lookup", new() { ["name"] = "Alpha" }),
            ("find_references", new() { ["symbol"] = "Alpha" }),
            ("repo_map", []),
        };

        foreach (var (tool, args) in probes)
        {
            var fromOwner = await DataAsync(owner, tool, args);
            var fromObserver = await DataAsync(observer, tool, args);
            fromObserver.ShouldBe(fromOwner, $"tool {tool} answered differently on the non-owner");
        }
    }

    [Fact]
    public async Task ClaimHeldElsewhere_ObserverPublishesItsBeat_TheFleetCanCountIt()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        var storeDir = SqueezrPaths.StoreDir(workspace);
        HoldClaim(storeDir);

        var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: ClaimAs(storeDir, ForeignPid()));
        await ServerAnswersAsync(harness);

        var ownBeat = DaemonHeartbeat.BeatPathFor(storeDir, DaemonHeartbeat.ServeWatchKind, Environment.ProcessId);
        File.Exists(ownBeat).ShouldBeTrue(
            "an observer that publishes nothing is the invisible duplicate this carrier exists to end");
        DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.ServeWatchKind).ShouldBeNull(
            "an observer wrote the per-kind heartbeat: that file belongs to the process running the watch loop");

        await harness.DisposeAsync();

        File.Exists(ownBeat).ShouldBeFalse("a departing observer left its beat behind");
    }

    [Fact]
    public async Task OwnerServe_GracefulStop_ReleasesClaim()
    {
        var workspace = NewIndexedWorkspace(watchEnabled: false);
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var harness = await McpTestHarness.StartAsync(workspace, watch: true);
        await WaitForEngineArmedAsync(workspace);
        ReadOwnerPid(storeDir).ShouldBe(Environment.ProcessId);

        await harness.DisposeAsync();

        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).ReadOwner()
            .ShouldBeNull("serve exit must release the claim");
        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).TryAcquire().IsOwner
            .ShouldBeTrue("the released claim must be immediately reacquirable");
    }

    private static int ReadOwnerPid(string storeDir) =>
        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).ReadOwner()!.Pid;

    private static WatchOwnerClaim ClaimAs(string storeDir, int pid) =>
        new(storeDir, DaemonHeartbeat.ServeWatchKind, null, null, null, pid);

    private static void HoldClaim(string storeDir)
    {
        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).TryAcquire().IsOwner
            .ShouldBeTrue("test failed to hold the claim that arranges the non-owner scenario");
    }

    // Any pid other than the test process works here: the harness serve under this pid never becomes the
    // owner in these scenarios, so the pid is recorded nowhere and never probed for liveness.
    private static int ForeignPid() => Environment.ProcessId ^ 0x3FFF;

    private static async Task<string> DataAsync(McpTestHarness harness, string tool, Dictionary<string, object?> args)
    {
        var result = await harness.Client.CallToolAsync(tool, args, cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(
            true, $"tool {tool} failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        return ParseEnvelope(result).GetProperty("data").GetRawText();
    }

    private static async Task ServerAnswersAsync(McpTestHarness harness)
    {
        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(
            true, $"index_status failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        ParseEnvelope(result).GetProperty("data");
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

    private static async Task WaitForEngineArmedAsync(string workspace)
    {
        var deadline = DateTime.UtcNow + EngineArmTimeout;
        while (DateTime.UtcNow < deadline && !EngineArmed(workspace))
        {
            await Task.Delay(50);
        }

        EngineArmed(workspace).ShouldBeTrue(
            $"watch engine did not arm within {EngineArmTimeout.TotalSeconds}s — a failed engine start is only reported on stderr");
    }

    private static bool EngineArmed(string workspace) =>
        DaemonHeartbeatReader.TryRead(
            SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.ServeWatchKind)?.Pid == Environment.ProcessId;

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
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { Enabled = watchEnabled, DebounceSeconds = 1 }
        });
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { Alpha Make() { return new Alpha(); } }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
