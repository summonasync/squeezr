using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Indexing.Watch;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Mcp;

// The owner scenario this feature exists for: a session that ended up observing keeps asking for the claim, so
// when the recorded owner dies the pending deferred embeddings move again with no 'squeezr fleet stop' in
// between. Every fixture drives the real adoption loop against a real store; only the clock and the pid probe
// are seams, because one process cannot die and keep answering at the same time.
public sealed class ServeWatchAdoptionTests
{
    private static readonly DateTime FrozenNow = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan EngineArmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ObserveSettle = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan StartupLineTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task RecordedOwnerDiesWhileSessionServes_SessionAdoptsAndDrainsPending()
    {
        var workspace = NewEmbedFreeIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);
        var provider = new CountingEmbeddingProvider();
        var probe = new OwnerProbe(alive: true);
        var stderr = new LineCapture();

        await using var harness = await McpTestHarness.StartAsync(
            workspace, watch: true,
            claimOverride: WedgedOwnerClaim(storeDir, probe),
            embeddingRuntime: Runtime(provider),
            stderr: stderr);

        await stderr.AwaitFor("with a stale beat under a live process", StartupLineTimeout);

        probe.Alive = false;

        await WaitForEngineArmedAsync(workspace);
        var adoptionLine = await stderr.AwaitFor("claim adopted from dead pid", StartupLineTimeout);
        adoptionLine.ShouldContain($"{StaleOwnerPid()}");
        await UntilAsync(EngineTimeout, () => PendingCount(workspace) == 0);
        provider.EmbeddedTextCount.ShouldBeGreaterThan(0);

        var adopted = WatchAdoptionEventFile.TryRead(storeDir).ShouldNotBeNull();
        adopted.AdopterPid.ShouldBe(Environment.ProcessId);
        adopted.DisplacedPid.ShouldBe(StaleOwnerPid());
    }

    [Fact]
    public async Task RecordedOwnerStaysAlive_ObserverNeverSteals()
    {
        var workspace = NewEmbedFreeIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var probe = new OwnerProbe(alive: true);
        var stderr = new LineCapture();

        await using var harness = await McpTestHarness.StartAsync(
            workspace, watch: true,
            claimOverride: WedgedOwnerClaim(storeDir, probe),
            embeddingRuntime: Runtime(new CountingEmbeddingProvider()),
            stderr: stderr);

        await stderr.AwaitFor("with a stale beat under a live process", StartupLineTimeout);
        await Task.Delay(ObserveSettle);

        DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.ServeWatchKind).ShouldBeNull(
            "the observer armed a watch engine although the recorded owner is still alive");
        WatchAdoptionEventFile.TryRead(storeDir).ShouldBeNull();
        WatchOwnerMarker.TryRead(storeDir)!.Pid.ShouldBe(
            StaleOwnerPid(), "the live owner's marker must survive the observer's adoption polls");
    }

    [Fact]
    public async Task OwnerReleasedBetweenPolls_ObserverTakesOverFreeStore_WithoutAdoptionProvenance()
    {
        var workspace = NewEmbedFreeIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var stderr = new LineCapture();
        var held = new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind);
        held.TryAcquire().IsOwner.ShouldBeTrue();

        var harness = await McpTestHarness.StartAsync(
            workspace, watch: true,
            claimOverride: FreeStoreClaim(storeDir),
            embeddingRuntime: Runtime(new CountingEmbeddingProvider()),
            stderr: stderr);
        try
        {
            await stderr.AwaitFor("owns this store", StartupLineTimeout);

            held.Release();

            await WaitForEngineArmedAsync(workspace);
            var takeoverLine = await stderr.AwaitFor("claim taken over", StartupLineTimeout);
            takeoverLine.ShouldContain("no owner");
            WatchAdoptionEventFile.TryRead(storeDir).ShouldBeNull(
                "claiming a store whose owner left gracefully is a fresh claim, not an adoption");
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private static WatchOwnerClaim FreeStoreClaim(string storeDir) => new(
        storeDir,
        DaemonHeartbeat.ServeWatchKind,
        null,
        null,
        null,
        ForeignPid());

    private static int ForeignPid() => Environment.ProcessId ^ 0x3FFF;

    private static WatchOwnerClaim WedgedOwnerClaim(string storeDir, OwnerProbe probe)
    {
        var marker = new WatchOwnerMarker(
            StaleOwnerPid(), Environment.MachineName, DaemonHeartbeat.ServeWatchKind, FrozenNow, FrozenNow);
        File.WriteAllText(
            WatchOwnerMarker.PathFor(storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));
        return new WatchOwnerClaim(
            storeDir,
            DaemonHeartbeat.ServeWatchKind,
            () => FrozenNow + TimeSpan.FromSeconds(60),
            _ => probe.Alive,
            null,
            Environment.ProcessId);
    }

    private sealed class OwnerProbe(bool alive)
    {
        public bool Alive { get; set; } = alive;
    }

    private static int StaleOwnerPid() => 4242;

    private static WatchEmbeddingRuntime Runtime(CountingEmbeddingProvider provider) =>
        new(provider, new FakeVectorStore(), new EmbeddingProviderResolution("ollama", null));

    private static async Task UntilAsync(TimeSpan timeout, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(200);
        }

        condition().ShouldBeTrue($"condition not met within {timeout.TotalSeconds}s");
    }

    private static async Task WaitForEngineArmedAsync(string workspace)
    {
        var deadline = DateTime.UtcNow + EngineArmTimeout;
        while (DateTime.UtcNow < deadline && !EngineArmed(workspace))
        {
            await Task.Delay(50);
        }

        EngineArmed(workspace).ShouldBeTrue(
            $"adopted watch engine did not arm within {EngineArmTimeout.TotalSeconds}s");
    }

    private static bool EngineArmed(string workspace) =>
        DaemonHeartbeatReader.TryRead(
            SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.ServeWatchKind)?.Pid == Environment.ProcessId;

    private static int PendingCount(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.CountPendingChunks(null);
    }

    private static string NewEmbedFreeIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        WriteFile(workspace, "repo/alpha.cs", "class Alpha { string Term = \"adoptneedle\"; }");
        WriteFile(workspace, "repo/beta.cs", "class Beta { string Term = \"adoptbeta7\"; }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public string ModelKey => TestEmbeddingKeys.TestModel;

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

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LineCapture : StringWriter
    {
        private readonly object _sync = new();

        public override void Write(string? value)
        {
            lock (_sync)
            {
                base.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                base.WriteLine(value);
            }
        }

        public string[] Lines()
        {
            lock (_sync)
            {
                return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        public async Task<string> AwaitFor(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hit = Lines().FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
                if (hit is not null)
                {
                    return hit;
                }

                await Task.Delay(50);
            }

            throw new InvalidOperationException(
                $"stderr never said '{needle}' within {timeout.TotalSeconds}s; it said:{Environment.NewLine}{string.Join(Environment.NewLine, Lines())}");
        }
    }
}
