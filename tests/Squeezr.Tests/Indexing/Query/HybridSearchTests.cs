using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class HybridSearchTests
{
    private const string Source = """
        class Calc
        {
            int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private const string SlugMark = "slugmark";

    private const string BigDoc = """
        # Release notes

        alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima
        mike november oscar papa quebec romeo sierra tango uniform victor whiskey
        xray yankee zulu one two three four five six seven eight nine ten twelve

        the planning slug carries slugmark for this fixture line
        alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima
        mike november oscar papa quebec romeo sierra tango uniform victor whiskey

        ## Appendix

        closing filler zulu one two three four five six seven eight nine twelve
        """;

    [Fact]
    public void Merge_KnownRanks_ProduceRrfScoresAndOrder()
    {
        SearchMatch Hit(string path) => new("demo", path, 1, 0.0, "snippet");
        var exact = new List<(string Key, SearchMatch Match)>
        {
            ("1:a", Hit("a.cs")),
            ("1:b", Hit("b.cs")),
            ("1:c", Hit("c.cs"))
        };
        var semantic = new List<(string Key, SearchMatch Match)>
        {
            ("1:c", Hit("c.cs")),
            ("1:d", Hit("d.cs"))
        };

        var merged = RrfMerger.Merge(exact, semantic);

        merged.Select(m => m.Path).ShouldBe(["c.cs", "a.cs", "b.cs", "d.cs"]);
        merged[0].Source.ShouldBe("both");
        merged[0].Score.ShouldBe(1.0 / 61 + 1.0 / 63, 1e-9);
        merged[1].Score.ShouldBe(1.0 / 61, 1e-9);
        merged[1].Source.ShouldBe("exact");
        merged[2].Score.ShouldBe(1.0 / 62, 1e-9);
        merged[3].Score.ShouldBe(1.0 / 62, 1e-9);
        merged[3].Source.ShouldBe("semantic");
    }

    [Fact]
    public void Hybrid_HitInBothLists_MergesToBothAndOutranksSingles()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var fileId = chunks.First().FileId;
        var chunk = ChunkContaining(chunks, 3);
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", fileId, repoId, chunk.StartLine, chunk.EndLine, 0.95)]
        };

        var response = Search(workspace, "Add", store);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Source.ShouldBe("both");
        hit.Score.ShouldBe(2.0 / 61, 1e-9);
        hit.Path.ShouldBe("Calc.cs");
        hit.Line.ShouldBe(3);
        store.SearchCalls.ShouldBe(1);
        store.LastTopK.ShouldBe(20);
    }

    [Fact]
    public void Search_WithTokenBudget_ReportsPerLegTokenUsage()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Available = false };

        var response = Search(workspace, "Add", store, maxTokens: 1);

        var usage = response.LegUsage.ShouldNotBeNull();
        usage.ExactTokens.ShouldBePositive();
        usage.SemanticTokens.ShouldBe(0);
        (usage.ExactTokens + usage.SemanticTokens).ShouldBe(response.TokensUsed);
    }

    [Fact]
    public void Search_BothSourceHit_CountsIntoExactLeg()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var fileId = chunks.First().FileId;
        var chunk = ChunkContaining(chunks, 3);
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", fileId, repoId, chunk.StartLine, chunk.EndLine, 0.95)]
        };

        var response = Search(workspace, "Add", store);

        response.Matches.ShouldContain(m => m.Source == "both");
        var usage = response.LegUsage.ShouldNotBeNull();
        usage.SemanticTokens.ShouldBe(0);
        (usage.ExactTokens + usage.SemanticTokens).ShouldBe(response.TokensUsed);
    }

    [Fact]
    public void Search_WithoutMatches_LegUsageStaysNull()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Available = false };

        var response = Search(workspace, "zzzznomatchzzz", store);

        response.Matches.ShouldBeEmpty();
        response.LegUsage.ShouldBeNull();
    }

    [Fact]
    public void Hybrid_StoreUnavailable_FallsBackToExactWithWarning()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Available = false };

        var response = Search(workspace, "Add", store);

        response.Warnings.ShouldHaveSingleItem().ShouldContain("falling back to exact");
        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldAllBe(m => m.Source == "exact");
        store.SearchCalls.ShouldBe(0);
    }

    [Fact]
    public void Hybrid_StoreEmpty_SilentlyExactOnly()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore();

        var response = Search(workspace, "Add", store);

        response.Warnings.ShouldBeEmpty();
        response.Matches.ShouldAllBe(m => m.Source == "exact");
        store.SearchCalls.ShouldBe(0);
    }

    [Fact]
    public void Hybrid_VectorOnlyHit_AppearsWithSemanticSource()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.8)]
        };

        var response = Search(workspace, "zzznotfound", store);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Source.ShouldBe("semantic");
        hit.Path.ShouldBe("Calc.cs");
        hit.Snippet.ShouldBe(chunk.Text.Trim());
    }

    [Fact]
    public void Hybrid_SemanticOnlyBelowFloor_WarnsButStillDeliversHits()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.3)]
        };

        var response = Search(workspace, "zzznotaword", store);

        response.Matches.ShouldNotBeEmpty();
        response.Warnings.ShouldHaveSingleItem().ShouldBe(
            "no exact-arm match; 1 semantic hit at low confidence — narrow with kind=code or prefix");
    }

    [Fact]
    public void Hybrid_SemanticOnlyAtOrAboveFloor_NoWarning()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.8)]
        };

        var response = Search(workspace, "zzznotaword", store);

        response.Warnings.ShouldBeEmpty();
        response.Matches.ShouldNotBeEmpty();
    }

    [Fact]
    public void Hybrid_BgeCodeProfileBelowFloor_HintsInsteadOfTheLowConfidenceSentence()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.3)]
        };

        var response = Search(workspace, "zzznotaword", store, modelKey: TestEmbeddingKeys.BgeCodeV1);

        response.Matches.ShouldNotBeEmpty();
        response.Warnings.ShouldHaveSingleItem().ShouldBe(
            $"weak-match floor uncalibrated for {TestEmbeddingKeys.BgeCodeV1} — no low-confidence claim made until a floor is measured for it");
        response.Warnings.ShouldNotContain(w => w.Contains("at low confidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Hybrid_BgeCodeProfileAboveFloor_HintsToo_TheFloorJudgesNothingUnderIt()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.95)]
        };

        var response = Search(workspace, "zzznotaword", store, modelKey: TestEmbeddingKeys.BgeCodeV1);

        response.Warnings.ShouldHaveSingleItem().ShouldContain("weak-match floor uncalibrated");
    }

    [Fact]
    public void Hybrid_UnregisteredOpenAiModelNameBelowFloor_HintsTheSameWay()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.3)]
        };

        var response = Search(workspace, "zzznotaword", store, modelKey: TestEmbeddingKeys.UnregisteredOpenAiModel);

        response.Warnings.ShouldHaveSingleItem().ShouldBe(
            $"weak-match floor uncalibrated for {TestEmbeddingKeys.UnregisteredOpenAiModel} — no low-confidence claim made until a floor is measured for it");
        response.Warnings.ShouldNotContain(w => w.Contains("at low confidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hybrid_NoProviderAtAll_AnswersInSilenceAboutConfidence()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.3)]
        };

        var response = await new SearchEngine(workspace).SearchHybridAsync(
            new SearchQuery("zzznotaword", false, null, null, null, null, null, null, null, null),
            null,
            store,
            CancellationToken.None);

        response.Matches.ShouldBeEmpty();
        response.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Hybrid_IdentifierFallbackHit_LowSemanticScore_NoWarning()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.2)]
        };

        var response = Search(workspace, "Add int", store);

        response.Matches.ShouldNotBeEmpty();
        response.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Hybrid_OversizedSemanticChunk_DeliversCappedPreviewWithoutTruncation()
    {
        var workspace = NewMarkdownWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First(c => c.Text.Contains(SlugMark));
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.9)]
        };

        var response = Search(workspace, "zznotaword", store);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Source.ShouldBe("semantic");
        hit.Line.ShouldBe(chunk.StartLine);
        hit.Snippet.Length.ShouldBeLessThanOrEqualTo(SemanticSnippet.MaxChars + 1);
        hit.Snippet.ShouldNotContain("Appendix");
        response.Truncated.ShouldBeFalse();
        response.TokensUsed.ShouldBe(TokenCounter.Estimate(hit.Snippet));
    }

    [Fact]
    public void Hybrid_ManyOversizedChunks_TopKFitsTheBudgetWithoutTruncation()
    {
        var workspace = NewMarkdownWorkspace(out var chunks, out var repoId);
        var store = new FakeVectorStore
        {
            Hits = chunks
                .Select((c, i) => new VectorHit((ulong)(i + 1), $"h{i}", c.FileId, repoId, c.StartLine, c.EndLine, 0.9))
                .ToList()
        };

        var response = Search(workspace, "zznotaword", store, maxTokens: 2000, topK: 8);

        response.Matches.Count.ShouldBe(3);
        response.Truncated.ShouldBeFalse();
        response.Matches.ShouldAllBe(m => m.Snippet.Length <= SemanticSnippet.MaxChars + 1);
        response.TokensUsed.ShouldBe(response.Matches.Sum(m => TokenCounter.Estimate(m.Snippet)));
    }

    [Fact]
    public async Task Resolver_OversizedChunk_SnippetLeadsWithTheMatchedLine()
    {
        var workspace = NewMarkdownWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First(c => c.Text.Contains(SlugMark));

        var hits = await SemanticHitsResolver.Resolve(
            workspace,
            new SearchQuery($"planning {SlugMark}", false, null, null, null, null, null, null),
            new FakeEmbeddingProvider(),
            new FakeVectorStore
            {
                Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.9)]
            },
            CancellationToken.None,
            null,
            SecretGuardReader.Load(workspace));

        var hit = hits.Hits.ShouldHaveSingleItem().Match;
        hit.Source.ShouldBe("semantic");
        hit.Line.ShouldBe(chunk.StartLine);
        hit.Snippet.ShouldStartWith("the planning slug carries slugmark for this fixture line");
        hit.Snippet.Length.ShouldBeLessThanOrEqualTo(SemanticSnippet.MaxChars + 1);
    }

    [Fact]
    public void Hybrid_RepoFilter_PropagatesToVectorStore()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.8)]
        };

        var response = Search(workspace, "Add", store, repo: "demo");

        store.LastFilter.ShouldBe(new VectorSearchFilter(repoId, null));
        response.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Hybrid_KindFilter_PropagatesToVectorStore()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Hits = [new VectorHit(1, "h", 999999, 1, 1, 2, 0.5)] };

        Search(workspace, "Add", store, kind: DocKindDetector.Code);

        store.LastFilter.ShouldBe(new VectorSearchFilter(null, DocKindDetector.Code));
    }

    [Fact]
    public void Hybrid_DefaultKind_SendsNoKindConditionToTheStore()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Hits = [new VectorHit(1, "h", 999999, 1, 1, 2, 0.5)] };

        Search(workspace, "Add", store);

        store.LastFilter.ShouldBe(new VectorSearchFilter(null, null));
    }

    [Fact]
    public void Hybrid_PartialCoverage_AddsOneLineWarning()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore();

        var response = Search(workspace, "Add", store, coverage: new SemanticCoverage(4, 1));

        response.Warnings.ShouldContain("semantic coverage: 75% (1 chunk pending embedding)");
    }

    [Fact]
    public void Hybrid_LargePendingCount_UsesPluralAndFlooredPercent()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore();

        var response = Search(workspace, "Add", store, coverage: new SemanticCoverage(4000, 3017));

        response.Warnings.ShouldContain("semantic coverage: 24% (3017 chunks pending embedding)");
    }

    [Fact]
    public void Hybrid_FullCoverage_StaysSilent()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore();

        var response = Search(workspace, "Add", store, coverage: new SemanticCoverage(4, 0));

        response.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Hybrid_NoChunksIndexed_CoverageWarningSuppressed()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore();

        var response = Search(workspace, "Add", store, coverage: new SemanticCoverage(0, 0));

        response.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Hybrid_StoreDownAndPartialCoverage_BothWarningsSpeak()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Available = false };

        var response = Search(workspace, "Add", store, coverage: new SemanticCoverage(4, 1));

        response.Warnings.ShouldContain(SearchEngine.VectorStoreUnavailableWarning);
        response.Warnings.ShouldContain("semantic coverage: 75% (1 chunk pending embedding)");
    }

    private static ChunkDocument ChunkContaining(List<ChunkDocument> chunks, int line)
    {
        var minStart = chunks.Where(c => c.StartLine <= line && line <= c.EndLine).Min(c => c.StartLine);
        return chunks.First(c => c.StartLine <= line && line <= c.EndLine && c.StartLine == minStart);
    }

    private static SearchResponse Search(
        string workspace, string query, FakeVectorStore store, string? repo = null,
        string? kind = null, SemanticCoverage? coverage = null,
        int? maxTokens = null, int? topK = null, string? modelKey = null)
    {
        var engine = new SearchEngine(workspace);
        return engine.SearchHybridAsync(
            new SearchQuery(query, false, null, repo, null, null, topK, maxTokens, null, kind),
            new FakeEmbeddingProvider { ModelKey = modelKey ?? TestEmbeddingKeys.Calibrated },
            store,
            CancellationToken.None,
            coverage).GetAwaiter().GetResult();
    }

    private static string NewIndexedWorkspace(out List<ChunkDocument> chunks, out uint repoId)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");

        var fullPath = Path.Combine(workspace, "repo", "Calc.cs");
        File.WriteAllText(fullPath, Source);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        repoId = store.FindRepo("demo")!.Id;
        chunks = store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
        chunks.ShouldNotBeEmpty();
        return workspace;
    }

    private static string NewMarkdownWorkspace(out List<ChunkDocument> chunks, out uint repoId)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");

        foreach (var name in new[] { "release-notes.md", "design-notes.md", "meeting-notes.md" })
        {
            File.WriteAllText(Path.Combine(workspace, "repo", name), BigDoc);
        }

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        repoId = store.FindRepo("demo")!.Id;
        chunks = store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
        chunks.ShouldNotBeEmpty();
        return workspace;
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public string ModelKey { get; init; } = TestEmbeddingKeys.Calibrated;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return new[] { bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f };
            }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public IReadOnlyList<VectorHit> Hits { get; set; } = [];

        public bool Available { get; set; } = true;

        public int SearchCalls { get; private set; }

        public int? LastTopK { get; private set; }

        public VectorSearchFilter? LastFilter { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
        {
            SearchCalls++;
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
