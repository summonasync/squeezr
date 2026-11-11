using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Workspace;

// The reconcile in one sentence: a copied store holds another branch's numbers, and this is the pass that puts
// them back on this one. Everything here runs against fakes for the provider and the collection, because the
// two facts the contract is made of — which chunks were re-embedded and whether the collection reaches the
// rows that claim a vector — have to be counted exactly, not measured against a live service.
[Collection("ProcessGlobals")]
public sealed class WorktreeReconcileTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-reconcile-a";
    private const string RepoName = "demo";

    private const string KeptSource = """
        public sealed class Kept
        {
            public int Value => 1;
        }
        """;

    private const string ShiftedSource = """
        public sealed class Shifted
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    // Three lines of prose go above the marker, so the number the lane has to answer with is not the number
    // the main worktree holds: an index that kept the copied coordinates would still say 3.
    private const string ShiftedSourceOnTheBranch = """
        public sealed class Shifted
        {
            // branch note
            // branch note
            // branch note
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeReconcileTests()
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
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    // The threshold in one number: every row a fill lands already reads 'ok', so a pass that only trusts the
    // database would flip a lane whose collection is empty to fresh. The claim is the obligation, and the
    // collection answers to it.
    [Fact]
    public void Reconcile_CopiedStoreWhoseCollectionIsShortOfItsOkRows_StaysPendingByNumbers()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var claimed = CountEmbeddedRows(registered.RootPath);
        claimed.ShouldNotBe(0, "the copied rows carry no claim would make this threshold meaningless");

        // The lane's collection lost half its points before this pass ran — a vector arm that died quietly is
        // the state reconcile exists to refuse.
        foreach (var id in collection.Points.Keys.Take(collection.Points.Count / 2).ToList())
        {
            collection.Points.Remove(id);
        }

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.CollectionShortCode);
        outcome.PassRan.ShouldBeTrue();
        outcome.Obligation.ShouldBeTrue();
        outcome.CollectionPoints.ShouldBe(collection.Points.Count);
        ((long)outcome.StoredChunks - outcome.PendingChunks).ShouldBeGreaterThan(outcome.CollectionPoints!.Value,
            "the rows that claim a vector outnumber the points that answer them — that gap is the pending");
    }

    // The count that did not answer is not a count of zero: a lane whose store cannot be read is pending with
    // its own code, never a reconciled green line printed over an unreadable collection.
    [Fact]
    public void Reconcile_CollectionDoesNotAnswer_IsPendingSilentAndNeverReconciled()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var silent = new FakeVectorStore { CountThrows = true };

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(silent, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.StoreSilentCode);
        outcome.CollectionPoints.ShouldBeNull();
        outcome.Obligation.ShouldBeTrue();
    }

    // The re-embed number the contract demands, measured against a real tree: only the file this branch
    // changed is embedded again, the untouched file keeps its chunks, and the marker lands on the line the
    // lane's own file holds.
    [Fact]
    public void Reconcile_BranchChangedOneFile_ReEmbedsOnlyThatFileAndAnswersWithTheBranchLineNumbers()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        File.WriteAllText(Path.Combine(child, "src", "Shifted.cs"), ShiftedSourceOnTheBranch);
        var diskLine = Array.IndexOf(
            File.ReadAllLines(Path.Combine(child, "src", "Shifted.cs")),
            "    public int Add(int a, int b)") + 1;
        diskLine.ShouldBe(6, "the assertion compares against the file on disk, not against the parent's index");
        var (registered, fill) = FillLane(main, child, collection);
        var provider = new FakeEmbeddingProvider();

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, provider));

        outcome.PassRan.ShouldBeTrue();
        outcome.Modified.ShouldBe(1, "the branch changed one file, and the parent's rows for it are re-stamped");
        var totalChunks = CountTotalRows(registered.RootPath);
        outcome.Reembedded.ShouldNotBeNull().ShouldBeLessThan(totalChunks,
            "unchanged chunks are not re-embedded: the pass re-did less than the store holds");
        outcome.Reembedded.ShouldBe(provider.EmbeddedTextCount,
            "the reported number is the number of texts the provider was handed");
        outcome.State.ShouldBe(WorktreeReconcileState.Reconciled);

        // the coordinate proof: the chunk that holds the marker sits where the lane's file puts it
        var chunkLines = ListChunks(registered.RootPath)
            .Where(chunk => chunk.Text.Contains("public int Add"))
            .Select(chunk => chunk.StartLine)
            .ToList();
        chunkLines.ShouldNotBeEmpty("the lane indexes its own checkout, so the method has rows here");
        chunkLines.ShouldContain(line => line <= diskLine,
            "the row that covers the marker belongs to this branch's copy of the file");

        // the marker file of a linked worktree is not a source file: three files on disk, three rows, no
        // '.git' row that would answer with the main worktree's absolute path.
        File.Exists(Path.Combine(child, ".git")).ShouldBeTrue("a linked worktree carries .git as a file");
        outcome.StoredFiles.ShouldBe(2, "the .git marker never enters the index");
    }

    // A loud failure of the vector arm leaves the copied database calling its chunks embedded. There must be
    // no path where such a lane reads as reconciled.
    [Fact]
    public void Reconcile_VectorArmRefusedLoudly_NeverReportsReconciled()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(
            registered, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.NotCopied(
                "the vector store did not answer", WorkspaceException.StorageUnavailableCode));
        foreach (var id in collection.Points.Keys.ToList())
        {
            collection.Points.Remove(id);
        }

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        fill.State.ShouldBe(WorktreeFillState.Filled, "the files landed; only the collection refused");
        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.CollectionShortCode);
        outcome.CollectionPoints.ShouldBe(0);
    }

    // The verb runs at every session start. A lane that already answers for its rows is measured, not
    // rescanned, and the measurement costs one count instead of a second pass over the tree.
    [Fact]
    public void Reconcile_LaneAlreadyReconciled_SecondRunOwesNoPass()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var provider = new FakeEmbeddingProvider();
        var first = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, provider));
        first.State.ShouldBe(WorktreeReconcileState.Reconciled);
        first.PassRan.ShouldBeTrue();
        var callsAfterFirst = provider.EmbedCallCount;

        var adopted = WorktreeRegistration.Register(child, null);
        var kept = WorktreeStoreFill.Fill(adopted, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        kept.State.ShouldBe(WorktreeFillState.Skipped);
        kept.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex);

        var second = WorktreeReconcile.Run(
            adopted, kept, noSemantic: false,
            () => Runtime(collection, provider));

        second.PassRan.ShouldBeFalse("no second pass over the same tree at the next session start");
        second.State.ShouldBe(WorktreeReconcileState.Reconciled);
        provider.EmbedCallCount.ShouldBe(callsAfterFirst, "a pass that was not owed asked for no embeddings");
    }

    // A lane born before its main worktree was indexed has nothing inherited and nothing short. That is a
    // deferred word, not a failure, and it must not leave the lane pending forever.
    [Fact]
    public void Reconcile_FillSkippedBecauseParentHoldsNoRows_DeferredWithoutAMeasurement()
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        WriteSource(main);
        new WorkspaceManager(main).Init("mainws");
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(registered, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, 0));
        fill.State.ShouldBe(WorktreeFillState.Skipped);
        fill.Skip.ShouldBe(WorktreeFillSkip.SourceHasNoRows);
        var collection = new FakeVectorStore();

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Deferred);
        outcome.PassRan.ShouldBeFalse();
        collection.CountCallCount.ShouldBe(0,
            "an empty lane has no collection question to ask, so it costs no round trip either");
    }

    [Fact]
    public void Reconcile_ReindexSkip_DeferredAndLeavesTheStoreEmpty()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(registered, copy: false, ModelKey(main));
        var provider = new FakeEmbeddingProvider();

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, provider));

        outcome.State.ShouldBe(WorktreeReconcileState.Deferred);
        outcome.PassRan.ShouldBeFalse();
        provider.EmbedCallCount.ShouldBe(0);
        StoreRows(registered.RootPath).ShouldBe((0, 0),
            "the explicit from-scratch option stays from-scratch: this verb does not index it for the user");
    }

    private (string Main, string Child) IndexedMainAndLane(out FakeVectorStore collection)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        WriteSource(main);
        new WorkspaceManager(main).Init("mainws");
        // The provider is named and its endpoint is dead: the fake runtime is what embeds here, and no test
        // path in this file may open a connection to the services on this machine.
        TestWorkspaceConfig.UseOllamaEmbeddings(main);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(main);
        new WorkspaceManager(main).AddRepo(".", RepoName);
        collection = new FakeVectorStore();
        new WorkspaceIndexer().Index(
            main,
            embeddings: new FakeEmbeddingProvider(),
            vectorStore: collection,
            embeddingProviderResolution: new EmbeddingProviderResolution("ollama", null));
        // the parent index run has to have produced points, or every threshold below measures an empty pair
        collection.UpsertedPointCount.ShouldNotBe(0);
        var child = AddWorktree(main, LaneBranch);
        return (main, child);
    }

    private (WorktreeRegistrationOutcome Registered, WorktreeFillOutcome Fill) FillLane(
        string main, string child, FakeVectorStore collection)
    {
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(
            registered, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(collection.Points.Count, collection.Points.Count,
                collection.Points.Count));
        fill.State.ShouldBe(WorktreeFillState.Filled);
        return (registered, fill);
    }

    // The runtime the verb would build for a lane whose config names ollama: the resolution travels with it,
    // because an 'auto' provider has to be resolved before a model key can be computed at all.
    private static WorktreeReconcileRuntime Runtime(IVectorStore store, IEmbeddingProvider provider) =>
        new(provider, store, new EmbeddingProviderResolution("ollama", null), null, []);

    private static Func<string> ModelKey(string main) =>
        () => EmbeddingModelKey.From(ConfigFile.Load(main).Embeddings);

    private static void WriteSource(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Kept.cs"), KeptSource);
        File.WriteAllText(Path.Combine(root, "src", "Shifted.cs"), ShiftedSource);
        TestGitRepo.Add(root, "src/Kept.cs");
        TestGitRepo.Add(root, "src/Shifted.cs");
        TestGitRepo.Commit(root, "seed");
    }

    private static string AddWorktree(string main, string branch)
    {
        var child = Path.Combine(main, ".lanes", "lane-a");
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    private static int CountEmbeddedRows(string root) =>
        ListChunks(root).Count(chunk => chunk.EmbeddingStatus == "ok");

    private static int CountPendingRows(string root) =>
        ListChunks(root).Count(chunk => chunk.EmbeddingStatus != "ok");

    private static int CountTotalRows(string root) => ListChunks(root).Count;

    private static (int Files, int Chunks) StoreRows(string root)
    {
        using var store = OpenStore(root);
        return (store.CountAllFiles(), store.GetSemanticCoverage().TotalChunks);
    }

    private static List<ChunkDocument> ListChunks(string root)
    {
        using var store = OpenStore(root);
        return store.ListFiles(store.FindRepo(RepoName)!.Id)
            .SelectMany(file => store.ListChunksByFile(file.Id))
            .ToList();
    }

    private static MetaStore OpenStore(string root) =>
        new(SqueezrPaths.MetaDbPath(root));

    private string Tracked(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public int EmbedCallCount { get; private set; }

        public int EmbeddedTextCount { get; private set; }

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            EmbedCallCount++;
            EmbeddedTextCount += texts.Count;
            return Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                var vector = new float[Dimensions];
                for (var i = 0; i < Dimensions; i++)
                {
                    vector[i] = bytes[i % bytes.Length] / 255f;
                }

                return vector;
            }).ToArray());
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Dictionary<ulong, VectorPoint> Points { get; } = new();

        public int CountCallCount { get; private set; }

        public bool CountThrows { get; init; }

        public int UpsertedPointCount { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            UpsertedPointCount += points.Count;
            foreach (var point in points)
            {
                Points[point.Id] = point;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct)
        {
            CountCallCount++;
            if (CountThrows)
            {
                throw new HttpRequestException("qdrant is down");
            }

            return Task.FromResult((long)Points.Count);
        }

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>(Points.Keys.ToList());

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
            foreach (var id in pointIds)
            {
                Points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            Points.Clear();
            return Task.CompletedTask;
        }
    }
}
