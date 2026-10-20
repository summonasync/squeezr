using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherEmbedDrainTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuietSettle = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task EditedFile_AfterCatchUpSettles_NextPassEmbedsTheNewChunks()
    {
        var workspace = NewWorkspace(embedBudget: 256);
        WriteFile(workspace, "repo/Alpha.cs", "class Alpha { string Term = \"drainold\"; }");
        WriteFile(workspace, "repo/Beta.cs", "class Beta { string Term = \"betaold\"; }");
        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);

        var provider = new CountingEmbeddingProvider();
        var recorder = new DrainRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, embeddings: Runtime(provider, new FakeVectorStore()));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            await UntilAsync(() => PendingCount(workspace) == 0);
            provider.Reset();

            WriteFile(workspace, "repo/Alpha.cs", "class Alpha { string Term = \"drainfresh\"; }");
            await UntilAsync(() => provider.EmbeddedTextCount > 0);
            await UntilAsync(() => PendingCount(workspace) == 0);
            await Task.Delay(QuietSettle);
            recorder.Faults.ShouldBeEmpty();

            ChunksOf(workspace).ShouldAllBe(c => c.EmbeddingStatus == "ok");
            new SearchEngine(workspace).Search(Query("drainfresh")).Matches.ShouldNotBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task SaturatedBudget_CatchUpReArms_EmptyBatchesUntilPendingZero()
    {
        var workspace = NewWorkspace(embedBudget: 3);
        for (var i = 0; i < 6; i++)
        {
            WriteFile(workspace, $"repo/File{i:D2}.cs", $"class Calc{i} {{ string Term = \"budgetold{i}\"; int Add{i}() => {i}; }}");
        }

        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThanOrEqualTo(9, "fixture must need more than two budgeted passes");

        var provider = new CountingEmbeddingProvider();
        var recorder = new DrainRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, embeddings: Runtime(provider, new FakeVectorStore()));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            await UntilAsync(() => PendingCount(workspace) == 0);

            var drains = recorder.Completed.Where(r => r.Embedded > 0).ToList();
            drains.Count.ShouldBeGreaterThanOrEqualTo(2, "a saturated budget must spread the catch-up over passes");
            drains.Count.ShouldBeLessThanOrEqualTo(pendingBefore, "no pass may embed more than the budget allows");
            drains.ShouldAllBe(r => r.Embedded <= 3);
            drains.Sum(r => r.Embedded).ShouldBe(pendingBefore);
            var distinctHashes = ChunksOf(workspace).Select(c => c.ChunkHash).Distinct().Count();
            provider.EmbeddedTextCount.ShouldBe(
                distinctHashes, "identical chunk texts share one vector, the rest is marking");
            recorder.Faults.ShouldBeEmpty();
            ChunksOf(workspace).ShouldAllBe(c => c.EmbeddingStatus == "ok");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task VectorStoreUnavailable_PassWarnsKeepsPendingAndServesOn()
    {
        var workspace = NewWorkspace(embedBudget: 256);
        WriteFile(workspace, "repo/Alpha.cs", ThreeMethodSource("Store", "storeold"));
        WriteFile(workspace, "repo/Beta.cs", ThreeMethodSource("Ledger", "ledgerold"));
        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);

        var provider = new CountingEmbeddingProvider();
        var recorder = new DrainRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, embeddings: Runtime(provider, new UnavailableVectorStore()));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            WriteFile(workspace, "repo/Alpha.cs", ThreeMethodSource("Store", "storefresh"));
            await UntilAsync(() => recorder.Completed.Count >= 2);

            recorder.Faults.ShouldBeEmpty();
            recorder.Completed.ShouldContain(r =>
                r.Warnings.Any(w => w.Contains("vector store is unavailable", StringComparison.Ordinal)));
            PendingCount(workspace).ShouldBe(
                pendingBefore, "the re-chunked file replaces its own rows: the queue must not shrink below the backlog");
            ChunksOf(workspace).ShouldAllBe(c => c.EmbeddingStatus != "ok");
            provider.EmbeddedTextCount.ShouldBe(0);

            WriteFile(workspace, "repo/Beta.cs", ThreeMethodSource("Ledger", "ledgerfresh"));
            await UntilAsync(() => recorder.Completed.Count >= 3);
            recorder.Faults.ShouldBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    private static string ThreeMethodSource(string className, string term) =>
        $"class {className}\n{{\n    string Term = \"{term}\";\n\n    int Sum(int a) {{ return a + 1; }}\n\n    int Mul(int a) {{ return a * 2; }}\n\n    int Sub(int a) {{ return a - 3; }}\n}}\n";

    [Fact]
    public async Task NoRuntime_PassStaysEmbedFree()
    {
        var workspace = NewWorkspace(embedBudget: 256);
        WriteFile(workspace, "repo/Alpha.cs", "class Alpha { string Term = \"freerunold\"; }");
        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);

        var recorder = new DrainRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            WriteFile(workspace, "repo/Alpha.cs", "class Alpha { string Term = \"freerunfresh\"; }");
            await UntilAsync(() => recorder.Completed.Count >= 1);
            await Task.Delay(QuietSettle);

            recorder.Faults.ShouldBeEmpty();
            PendingCount(workspace).ShouldBeGreaterThan(0);
            recorder.Completed.ShouldNotContain(r => r.Embedded > 0);
            new SearchEngine(workspace).Search(Query("freerunfresh")).Matches.ShouldNotBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task PartialDrain_ProviderDiedMidPass_ReArmsExactlyOnce()
    {
        var workspace = NewWorkspace(embedBudget: 256);
        for (var i = 0; i < 8; i++)
        {
            WriteFile(workspace, $"repo/Dead{i:D2}.cs", ThreeMethodSource($"Dead{i}", $"deadold{i}"));
        }

        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThanOrEqualTo(33, "fixture must span more than one embed batch");

        var provider = new FailsFromSecondCallProvider();
        var recorder = new DrainRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, embeddings: Runtime(provider, new FakeVectorStore()));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            await UntilAsync(() => recorder.Completed.Count >= 2);
            await Task.Delay(QuietSettle);
            recorder.Faults.ShouldBeEmpty();
            recorder.Completed.Count.ShouldBe(2, "one partial pass buys exactly one retry, a retry that fails at the tail buys nothing");

            var partial = recorder.Completed.Single(r => r.Embedded > 0);
            partial.EmbeddingsCompleted.ShouldBeFalse();
            recorder.Completed.ShouldContain(r => r.Embedded == 0 && !r.EmbeddingsCompleted);
            PendingCount(workspace).ShouldBe(pendingBefore - partial.Embedded);
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task DimensionMismatch_DrainDiesPassesStayEmbedFree()
    {
        var workspace = NewWorkspace(embedBudget: 256);
        WriteFile(workspace, "repo/Alpha.cs", ThreeMethodSource("Alpha", "misold"));
        new WorkspaceIndexer().Index(workspace);

        var provider = new CountingEmbeddingProvider();
        var recorder = new DrainRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, embeddings: Runtime(provider, new MismatchVectorStore()));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            WriteFile(workspace, "repo/Alpha.cs", ThreeMethodSource("Alpha", "misfresh"));
            await UntilAsync(() => recorder.Faults.Any(f =>
                f.Contains("watch embed drain disabled", StringComparison.Ordinal)));
            await UntilAsync(
                () => new SearchEngine(workspace).Search(Query("misfresh")).Matches.Count > 0,
                "the mismatch pass must not hold the edit hostage: an embed-free pass indexes it");

            provider.EmbeddedTextCount.ShouldBe(0);
            PendingCount(workspace).ShouldBeGreaterThan(0);

            WriteFile(workspace, "repo/Beta.cs", ThreeMethodSource("Beta", "misagain"));
            await UntilAsync(() => new SearchEngine(workspace).Search(Query("misagain")).Matches.Count > 0);
            recorder.Faults.Count(f => f.Contains("watch embed drain disabled", StringComparison.Ordinal))
                .ShouldBe(1, "the fault line repeats only when it changes");
            provider.EmbeddedTextCount.ShouldBe(0);
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    private static Task<WatchEmbeddingRuntime?> Runtime(IEmbeddingProvider provider, IVectorStore store) =>
        Task.FromResult<WatchEmbeddingRuntime?>(
            new WatchEmbeddingRuntime(provider, store, new EmbeddingProviderResolution("onnx", null)));

    private static async Task UntilAsync(Func<bool> condition, string? what = null)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        condition().ShouldBeTrue($"condition not met within {WaitTimeout.TotalSeconds}s" +
                                 (what is null ? "" : $": {what}"));
    }

    private static async Task StopWatcherAsync(WorkspaceWatcher watcher, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        var stop = watcher.StopAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!stop.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        stop.IsCompleted.ShouldBeTrue("watcher did not stop within 5s");
    }

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

    private static string NewWorkspace(int embedBudget)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1, EmbedBudgetPerPass = embedBudget }
        });
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

    private static List<ChunkDocument> ChunksOf(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        return store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
    }

    private sealed class DrainRecorder : IWatchStatus
    {
        private readonly object _gate = new();
        private readonly List<IndexResult> _completed = [];
        private readonly List<string> _faults = [];

        public IReadOnlyList<IndexResult> Completed
        {
            get
            {
                lock (_gate)
                {
                    return _completed.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Faults
        {
            get
            {
                lock (_gate)
                {
                    return _faults.ToArray();
                }
            }
        }

        public void PassStarted(int changedPaths)
        {
        }

        public void PassCompleted(IndexResult result)
        {
            lock (_gate)
            {
                _completed.Add(result);
            }
        }

        public void RetryScheduled(TimeSpan delay)
        {
        }

        public void Faulted(string error)
        {
            lock (_gate)
            {
                _faults.Add(error);
            }
        }
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public int EmbeddedTextCount { get; private set; }

        public void Reset() => EmbeddedTextCount = 0;

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

    private sealed class UnavailableVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);

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

    private sealed class MismatchVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) =>
            throw new VectorDimensionMismatchException("mismatch-coll", 1024, dimensions);

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

    private sealed class FailsFromSecondCallProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        private int _calls;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) >= 2)
            {
                throw new IOException("embedding provider died mid-drain");
            }

            return Task.FromResult(texts.Select(text =>
            {
                var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
                return Enumerable.Range(0, Dimensions).Select(i => bytes[i % bytes.Length] / 255f).ToArray();
            }).ToArray());
        }
    }
}
