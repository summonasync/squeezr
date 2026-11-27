using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Cli;

// Every verb that ends a store comes through this door, so the order it removes things in is measured here
// rather than argued in prose: row and store dir first, and the collection is only asked for once nothing in
// the registry can still be reading it.
[Collection("ProcessGlobals")]
public sealed class WorkspaceStoreDropperTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceStoreDropperTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = Tracked(TestDirs.NewTempDir());
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        Directory.CreateDirectory(_home);
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DropAsync_RowAndStoreDirAreGone_WhenTheCollectionIsAsked()
    {
        var root = RegisterWorkspace("lane-a", out var storeKey, out var storeDir);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "the checkout is not this verb's to delete");
        var handle = new RecordingHandle(storeDir);

        var result = await WorkspaceStoreDropper.DropAsync(storeKey, root, handle, CancellationToken.None);

        handle.StoreDirExistedAtDelete.ShouldBe(false, "the store goes before the collection is touched");
        handle.RowPresentAtDelete.ShouldBe(false, "and so does the row that named it");
        handle.DeletedCount.ShouldBe(1);
        result.Collection.ShouldBe(QdrantVectorStore.GetCollectionName(root));
        result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionRemoved);
        result.RemovedPaths.ShouldHaveSingleItem().ShouldBe(storeDir);
        Directory.Exists(storeDir).ShouldBeFalse();
        WorkspaceRegistry.Load().ShouldBeEmpty();
        File.Exists(Path.Combine(root, "keep.txt")).ShouldBeTrue(
            "a store death leaves the user's tree alone; only the store is this verb's");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task DropAsync_StoreDirUndeletable_RefusesAndAsksNothingOfTheCollection()
    {
        var root = RegisterWorkspace("lane-b", out var storeKey, out var storeDir);
        var handle = new RecordingHandle(storeDir);
        File.SetUnixFileMode(storeDir, UnixFileMode.None);

        var failure = await Should.ThrowAsync<WorkspaceStoreRemovalException>(
            () => WorkspaceStoreDropper.DropAsync(storeKey, root, handle, CancellationToken.None));

        File.SetUnixFileMode(
            storeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        failure.StoreKey.ShouldBe(storeKey);
        handle.DeletedCount.ShouldBe(0,
            "a store that could not be removed keeps the vectors of a workspace that still exists");
        WorkspaceRegistry.Load().ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        Directory.Exists(storeDir).ShouldBeTrue();
    }

    [Fact]
    public async Task DropAsync_WithoutHandle_ClearsRowAndStore_AndSaysNoCollectionWasAsked()
    {
        var root = RegisterWorkspace("lane-c", out var storeKey, out var storeDir);

        var result = await WorkspaceStoreDropper.DropAsync(storeKey, root, null, CancellationToken.None);

        result.Collection.ShouldBeNull();
        result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionNotConfigured);
        result.RemovedPaths.ShouldHaveSingleItem().ShouldBe(storeDir);
        Directory.Exists(storeDir).ShouldBeFalse();
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public async Task DropAsync_QdrantNotAnswering_ReportsUnavailable_AndKeepsTheStoreGone()
    {
        var root = RegisterWorkspace("lane-e", out var storeKey, out var storeDir);
        var handle = new RecordingHandle(storeDir) { Available = false };

        var result = await WorkspaceStoreDropper.DropAsync(storeKey, root, handle, CancellationToken.None);

        result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionUnavailable);
        handle.DeletedCount.ShouldBe(0, "an endpoint that does not answer is not told to delete");
        WorkspaceRegistry.Load().ShouldBeEmpty("the store is gone; the report names which half failed");
    }

    [Fact]
    public async Task DropAsync_DeleteRefusedByEndpoint_ReportsFailed_AndRowStaysGone()
    {
        var root = RegisterWorkspace("lane-f", out var storeKey, out var storeDir);
        var handle = new RecordingHandle(storeDir) { FailDelete = true };

        var result = await WorkspaceStoreDropper.DropAsync(storeKey, root, handle, CancellationToken.None);

        result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionFailed);
        handle.DeletedCount.ShouldBe(1);
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public async Task DropByStoreKeyAsync_UnknownKey_RefusesWithTheRegistrySentence()
    {
        var failure = await Should.ThrowAsync<WorkspaceException>(
            () => WorkspaceStoreDropper.DropByStoreKeyAsync("nobody-1a2b3c4d", CancellationToken.None));

        failure.Message.ShouldContain("No workspace is registered with store key 'nobody-1a2b3c4d'");
    }

    [Fact]
    public async Task DropByStoreKeyAsync_RegisteredRow_TakesTheSameDoorAsTheWorktreeVerb()
    {
        var root = RegisterWorkspace("lane-g", out var storeKey, out var storeDir);
        WriteDeadEndpointConfig(root);

        var result = await WorkspaceStoreDropper.DropByStoreKeyAsync(storeKey, CancellationToken.None);

        Directory.Exists(storeDir).ShouldBeFalse();
        WorkspaceRegistry.Load().ShouldBeEmpty();
        result.Collection.ShouldBe(QdrantVectorStore.GetCollectionName(root),
            "the store-key arm resolves the root off the row, so both verbs name the same collection");
        result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionUnavailable,
            "a config pointing at an endpoint nobody answers is reported, not swallowed");
    }

    [Fact]
    public async Task DropByStoreKeyAsync_ConfigUnreadable_ReportsNotConfigured_WithoutTouchingAnyStore()
    {
        var root = RegisterWorkspace("lane-h", out var storeKey, out var storeDir);

        var result = await WorkspaceStoreDropper.DropByStoreKeyAsync(storeKey, CancellationToken.None);

        result.Collection.ShouldBeNull();
        result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionNotConfigured);
        Directory.Exists(storeDir).ShouldBeFalse();
    }

    private string RegisterWorkspace(string name, out string storeKey, out string storeDir)
    {
        var root = Tracked(TestDirs.NewTempDir());
        storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "x");
        return root;
    }

    // The config is what the door reads to find the endpoint; this one names a port nothing listens on, so
    // the live Qdrant of this machine is never in the path of a test.
    private static void WriteDeadEndpointConfig(string root) =>
        SemanticConfigFile.Save(root, SemanticConfig.Bind(SqueezrConfig.CreateDefault()) with
        {
            Qdrant = new QdrantConfig { Endpoint = TestWorkspaceConfig.DeadQdrantEndpoint }
        });

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }

    private sealed class RecordingHandle(string storeDir) : IVectorStore
    {
        public bool Available { get; set; } = true;

        public bool FailDelete { get; set; }

        public int DeletedCount { get; private set; }

        public bool? StoreDirExistedAtDelete { get; private set; }

        public bool? RowPresentAtDelete { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
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

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            DeletedCount++;
            StoreDirExistedAtDelete = Directory.Exists(storeDir);
            RowPresentAtDelete = WorkspaceRegistry.Load().Count > 0;
            if (FailDelete)
            {
                throw new IOException("qdrant refused the delete");
            }

            return Task.CompletedTask;
        }
    }
}
