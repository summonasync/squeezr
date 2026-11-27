using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class SemanticHitsResolverScopeTests
{
    private const string Source = """
        public class Probe
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    [Theory]
    [InlineData(4, 60)]
    [InlineData(2, 120)]
    [InlineData(1, 160)]
    public async Task Resolve_SmallFileShare_AsksForMoreThanBaseTopKWithinTheCeiling(int scopeSize, int expectedTopK)
    {
        var fixture = NewFixtureWorkspace();
        var store = new FakeVectorStore();
        var scope = fixture.DemoFilesByPath.Values.Take(scopeSize).ToHashSet();

        var arm = await Resolve(fixture.Root, store, scope);

        store.LastTopK.ShouldBe(expectedTopK);
        store.LastTopK!.Value.ShouldBeGreaterThan(ScopedVectorBudget.BaseTopK);
        store.LastTopK.Value.ShouldBeLessThanOrEqualTo(ScopedVectorBudget.Ceiling);
        store.LastTopK.Value.ShouldBe(ScopedVectorBudget.For(scopeSize, fixture.WorkspaceFileCount));
        arm.RequestedTopK.ShouldBe(expectedTopK);
    }

    [Fact]
    public async Task Resolve_FileScopeCoveringEveryWorkspaceFile_RequestsExactlyBaseTopK()
    {
        var fixture = NewFixtureWorkspace();
        var store = new FakeVectorStore();

        await Resolve(fixture.Root, store, fixture.DemoFilesByPath.Values.Concat(fixture.ExtraFileIds).ToHashSet());

        store.LastTopK.ShouldBe(ScopedVectorBudget.BaseTopK);
    }

    [Fact]
    public async Task Resolve_RepoScopedFileScope_DerivesTheShareFromTheRepoNotTheWorkspace()
    {
        var fixture = NewFixtureWorkspace();
        var store = new FakeVectorStore();

        var arm = await Resolve(fixture.Root, store, fixture.DemoFilesByPath.Values.Take(2).ToHashSet(), repo: "demo");

        store.LastTopK.ShouldBe(ScopedVectorBudget.For(2, 8));
        store.LastTopK!.Value.ShouldBe(80);
        store.LastFilter.ShouldBe(new VectorSearchFilter(fixture.DemoRepoId, null));
        arm.RequestedTopK.ShouldBe(80);
    }

    [Fact]
    public async Task Resolve_Unscoped_RequestsExactlyBaseTopKAndKeepsStoreOrder()
    {
        var fixture = NewFixtureWorkspace();
        var util = fixture.DemoFilesByPath["lib/util1.js"];
        var alpha = fixture.DemoFilesByPath["src/Core/Alpha.cs"];
        var beta = fixture.DemoFilesByPath["src/Cli/Beta.cs"];
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(fixture.Root));
        var utilChunk = FirstChunk(meta, util);
        var alphaChunk = FirstChunk(meta, alpha);
        var betaChunk = FirstChunk(meta, beta);
        var store = new FakeVectorStore
        {
            Hits =
            [
                Hit(1, util, fixture.DemoRepoId, utilChunk, 0.9),
                Hit(2, alpha, fixture.DemoRepoId, alphaChunk, 0.8),
                Hit(3, beta, fixture.DemoRepoId, betaChunk, 0.7)
            ]
        };

        var arm = await Resolve(fixture.Root, store, null);

        store.LastTopK.ShouldBe(ScopedVectorBudget.BaseTopK);
        store.LastTopK.ShouldBe(20);
        arm.RequestedTopK.ShouldBe(20);
        arm.StoreCandidates.ShouldBe(3);
        arm.InScopeCandidates.ShouldBe(3);
        arm.Hits.Select(h => h.Match.Path).ShouldBe(["lib/util1.js", "src/Core/Alpha.cs", "src/Cli/Beta.cs"]);
        arm.Hits.Select(h => h.Match.Score).ShouldBe([0.9, 0.8, 0.7]);
        arm.Hits.Select(h => h.Key).ShouldBe(
        [
            $"{util}:{utilChunk.StartLine}",
            $"{alpha}:{alphaChunk.StartLine}",
            $"{beta}:{betaChunk.StartLine}"
        ]);
        arm.Hits.ShouldAllBe(h => h.Match.Source == "semantic");
    }

    [Fact]
    public void Hybrid_PrefixScope_SemanticSurvivorsMatchTheExactArmScopeSet()
    {
        var fixture = NewFixtureWorkspace();
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(fixture.Root));
        var lib = fixture.DemoFilesByPath["lib/util1.js"];
        var libChunk = FirstChunk(meta, lib);
        var srcHits = fixture.DemoFilesByPath
            .Where(kv => kv.Key.StartsWith("src/"))
            .Select(kv => (kv.Key, FileId: kv.Value, Chunk: FirstChunk(meta, kv.Value)))
            .ToList();
        var store = new FakeVectorStore
        {
            Hits =
            [
                Hit(1, lib, fixture.DemoRepoId, libChunk, 0.95),
                ..srcHits.Select((s, i) => Hit((ulong)(i + 2), s.FileId, fixture.DemoRepoId, s.Chunk, 0.8 - i * 0.1))
            ]
        };
        var engine = new SearchEngine(fixture.Root);

        var scoped = Hybrid(fixture.Root, engine, store, "src/");

        var expectedScope = FileScope.Resolve(meta, file => PathPrefix.Matches(PathPrefix.Normalize("src/")!, file.Path));
        var survivors = scoped.Matches.Where(m => m.Source == "semantic").ToList();
        survivors.Select(m => m.Path).ShouldBe(srcHits.Select(s => s.Key).ToList());
        survivors.ShouldNotBeEmpty();
        survivors.All(m => expectedScope.Contains(fixture.DemoFilesByPath[m.Path])).ShouldBeTrue();
        store.LastTopK.ShouldBe(ScopedVectorBudget.For(2, fixture.WorkspaceFileCount));

        var caseVariant = Hybrid(fixture.Root, engine, store, "SRC/");
        caseVariant.Matches.Where(m => m.Source == "semantic").Select(m => m.Path)
            .ShouldBe(survivors.Select(m => m.Path));
        store.LastTopK.ShouldBe(ScopedVectorBudget.For(2, fixture.WorkspaceFileCount));
    }

    private sealed record WorkspaceFixture(
        string Root,
        uint DemoRepoId,
        Dictionary<string, uint> DemoFilesByPath,
        IReadOnlyList<uint> ExtraFileIds,
        int WorkspaceFileCount);

    private static VectorHit Hit(ulong id, uint fileId, uint repoId, ChunkDocument chunk, double score) =>
        new(id, "h", fileId, repoId, chunk.StartLine, chunk.EndLine, score);

    private static ChunkDocument FirstChunk(MetaStore meta, uint fileId) => meta.ListChunksByFile(fileId).First();

    private static Task<SemanticArm> Resolve(
        string workspace, IVectorStore store, IReadOnlySet<uint>? fileScope, string? repo = null) =>
        SemanticHitsResolver.Resolve(
            workspace,
            new SearchQuery("zznotaword", false, null, repo, null, null, null, null),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None,
            fileScope,
            SecretGuardReader.Load(workspace));

    private static SearchResponse Hybrid(string workspaceRoot, SearchEngine engine, FakeVectorStore store, string? prefix) =>
        engine.SearchHybridAsync(new SearchQuery("whatever", false, null, null, null, null, null, null, prefix), TestVectorPhase.Arm(workspaceRoot, new FakeEmbeddingProvider(), store), CancellationToken.None).GetAwaiter().GetResult();

    private static WorkspaceFixture NewFixtureWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);

        foreach (var relative in DemoRepoFiles.Concat(ExtraRepoFiles))
        {
            var fullPath = Path.Combine([workspace, .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, Source);
        }

        manager.AddRepo("repo", "demo");
        manager.AddRepo("extra", "extrarepo");
        new WorkspaceIndexer().Index(workspace, vectorPhase: TestVectorPhase.Runtime(new FakeEmbeddingProvider(), new FakeVectorStore()));

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var demoRepoId = store.FindRepo("demo")!.Id;
        var demoFiles = store.ListFiles(demoRepoId).ToDictionary(f => f.Path, f => f.Id);
        var extraRepoId = store.FindRepo("extrarepo")!.Id;
        var extraFiles = store.ListFiles(extraRepoId).Select(f => f.Id).ToList();
        var workspaceFileCount = store.CountAllFiles();
        return new WorkspaceFixture(workspace, demoRepoId, demoFiles, extraFiles, workspaceFileCount);
    }

    private static readonly string[] DemoRepoFiles =
    [
        "repo/src/Core/Alpha.cs",
        "repo/src/Cli/Beta.cs",
        "repo/lib/util1.js",
        "repo/lib/util2.js",
        "repo/docs/Guide1.cs",
        "repo/docs/Guide2.cs",
        "repo/docs/Guide3.cs",
        "repo/docs/Guide4.cs"
    ];

    private static readonly string[] ExtraRepoFiles =
    [
        "extra/Extra1.cs",
        "extra/Extra2.cs",
        "extra/Extra3.cs",
        "extra/Extra4.cs"
    ];

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.Calibrated;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f, 0.4f }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public IReadOnlyList<VectorHit> Hits { get; set; } = [];

        public int? LastTopK { get; private set; }

        public VectorSearchFilter? LastFilter { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
        {
            LastTopK = topK;
            LastFilter = filter;
            return Task.FromResult(Hits);
        }

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Hits.Count);

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
}
