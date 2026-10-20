using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public sealed class ServeEmbedDrainTests
{
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EngineArmTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ServeWithWatch_DrainsPendingChunksThroughTheInjectedRuntime()
    {
        var workspace = NewEmbedFreeIndexedWorkspace();
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);
        var provider = new CountingEmbeddingProvider();
        await using var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, embeddingRuntime: Runtime(provider));

        await WaitForEngineArmedAsync(workspace);
        await UntilAsync(EngineTimeout, () => PendingCount(workspace) == 0);
        var embeddedByCatchUp = provider.EmbeddedTextCount;
        embeddedByCatchUp.ShouldBeGreaterThan(0);

        WriteFile(workspace, "repo/alpha.cs", "class Alpha { string Term = \"drainserved7\"; }");
        await UntilAsync(EngineTimeout, () => provider.EmbeddedTextCount > embeddedByCatchUp);
        await UntilAsync(EngineTimeout, () => PendingCount(workspace) == 0);

        (await PendingFromIndexStatusAsync(harness)).ShouldBe(0);
    }

    [Fact]
    public async Task NoEmbedWatch_PassIndexesEditsButPendingStays()
    {
        var workspace = NewEmbedFreeIndexedWorkspace();
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);
        await using var harness = await McpTestHarness.StartAsync(workspace, watch: true, embedWatch: false);

        await WaitForEngineArmedAsync(workspace);
        WriteFile(workspace, "repo/alpha.cs", "class Alpha { string Term = \"embedfree9\"; }");

        await UntilAsync(EngineTimeout, async () => await HasHitAsync(harness, "embedfree9"));
        PendingCount(workspace).ShouldBeGreaterThan(0);
        (await PendingFromIndexStatusAsync(harness)).ShouldBeGreaterThan(0);
    }

    private static WatchEmbeddingRuntime Runtime(CountingEmbeddingProvider provider) =>
        new(provider, new FakeVectorStore(), new EmbeddingProviderResolution("onnx", null));

    private static async Task<int> PendingFromIndexStatusAsync(McpTestHarness harness)
    {
        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true, string.Join(" ", result.Content.Select(b => b.ToString())));
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("embeddings").GetProperty("pending").GetInt32();
    }

    private static async Task<bool> HasHitAsync(McpTestHarness harness, string term)
    {
        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = term },
            cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true, string.Join(" ", result.Content.Select(b => b.ToString())));
        return JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!).RootElement
            .GetProperty("data").EnumerateArray().Any();
    }

    private static async Task UntilAsync(TimeSpan timeout, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(200);
        }

        condition().ShouldBeTrue($"condition not met within {timeout.TotalSeconds}s");
    }

    private static async Task UntilAsync(TimeSpan timeout, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !await condition())
        {
            await Task.Delay(200);
        }

        (await condition()).ShouldBeTrue($"condition not met within {timeout.TotalSeconds}s");
    }

    private static async Task WaitForEngineArmedAsync(string workspace)
    {
        var deadline = DateTime.UtcNow + EngineArmTimeout;
        while (DateTime.UtcNow < deadline && !EngineArmed(workspace))
        {
            await Task.Delay(50);
        }

        EngineArmed(workspace).ShouldBeTrue("watch engine did not arm within timeout");
    }

    private static bool EngineArmed(string workspace) =>
        DaemonHeartbeatReader.TryRead(
            SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.ServeWatchKind)?.Pid == Environment.ProcessId;

    private static string NewEmbedFreeIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        WriteFile(workspace, "repo/alpha.cs", "class Alpha { string Term = \"uniqneedle\"; }");
        WriteFile(workspace, "repo/beta.cs", "class Beta { string Term = \"betaneedle\"; }");
        new Squeezr.Core.Indexing.WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static int PendingCount(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.CountPendingChunks(null);
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public int EmbeddedTextCount { get; private set; }

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            EmbeddedTextCount += texts.Count;
            return Task.FromResult(texts.Select(text =>
            {
                var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
                return Enumerable.Range(0, Dimensions).Select(i => bytes[i % bytes.Length] / 255f).ToArray();
            }).ToArray());
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
