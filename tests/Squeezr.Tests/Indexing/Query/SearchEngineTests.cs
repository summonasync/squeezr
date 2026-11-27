using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class SearchEngineTests
{
    private const string FixtureContent = "workspacemanager";

    [Fact]
    public void Literal_FindsMatchWithCoordinates()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths.ShouldContain("src/WorkspaceManager.cs");
        paths.ShouldContain("src/worker.cs");
        response.Truncated.ShouldBeFalse();

        var hit = response.Matches.First(m => m.Path == "src/WorkspaceManager.cs");
        hit.Repo.ShouldBe("demo");
        hit.Line.ShouldBe(1);
        hit.Snippet.ShouldContain("class WorkspaceManager");
    }

    [Fact]
    public void SmartCase_LowerQueryMatchesAnyCase_UpperQueryDoesNot_FlagForcesInsensitive()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, null)).Matches.ShouldNotBeEmpty();

        engine.Search(new SearchQuery("Workspacemanager", false, null, null, null, null, null, null)).Matches.ShouldBeEmpty();

        engine.Search(new SearchQuery("Workspacemanager", false, false, null, null, null, null, null)).Matches.ShouldNotBeEmpty();
    }

    [Fact]
    public void ShortLiteral_FallsBackToFullScan()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("Wo", false, null, null, null, null, null, null));

        response.Matches.ShouldHaveSingleItem().Path.ShouldBe("src/WorkspaceManager.cs");
    }

    [Fact]
    public void Regex_WithExtractableTrigrams_FindsMatches()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("W.rkspaceM.nager", true, null, null, null, null, null, null));

        response.Matches.ShouldHaveSingleItem().Path.ShouldBe("src/WorkspaceManager.cs");
    }

    [Fact]
    public void Regex_WithoutTrigrams_DegradesToFullScan()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("\\d+", true, null, null, null, null, null, null));

        response.Matches.ShouldBeEmpty();
    }

    [Fact]
    public void Filters_NarrowResults()
    {
        var workspace = NewIndexedWorkspace(addSecondRepo: true);
        var engine = new SearchEngine(workspace);
        var baseQuery = new SearchQuery("workspacemanager", false, null, null, null, null, null, null);

        engine.Search(baseQuery with { Repo = "other" }).Matches.ShouldHaveSingleItem().Repo.ShouldBe("other");
        engine.Search(baseQuery with { PathGlob = "**/lib/**" }).Matches.ShouldHaveSingleItem().Path.ShouldBe("lib/util.js");
        engine.Search(baseQuery with { Language = "markdown" }).Matches.ShouldHaveSingleItem().Path.ShouldBe("docs/notes.md");
    }

    [Fact]
    public async Task PathGlob_ExactArmStaysInsideGlob()
    {
        var fixture = NewGlobFixture();
        var engine = new SearchEngine(fixture.Workspace);

        var response = await engine.SearchHybridAsync(
            GlobQuery("needle") with { PathGlob = "a/**" },
            TestVectorPhase.Arm(fixture.Workspace, new FakeEmbeddingProvider(), new FakeVectorStore()),
                        CancellationToken.None);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Path.ShouldBe("a/x.js");
        hit.Source.ShouldBe("exact");
    }

    [Fact]
    public async Task PathGlob_TokenArmStaysInsideGlob()
    {
        var fixture = NewGlobFixture();
        var engine = new SearchEngine(fixture.Workspace);

        var response = await engine.SearchHybridAsync(
            GlobQuery("collector lookup") with { PathGlob = "a/**" },
            TestVectorPhase.Arm(fixture.Workspace, new FakeEmbeddingProvider(), new FakeVectorStore()),
                        CancellationToken.None);

        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldAllBe(m => m.Path == "a/x.js");
    }

    [Fact]
    public void PathGlob_EmptyingPrefixSubtree_AnswersEmptyWithoutSubtreeError()
    {
        var fixture = NewGlobFixture();
        var engine = new SearchEngine(fixture.Workspace);

        var response = engine.Search(GlobQuery("needle") with { Prefix = "a/", PathGlob = "**/*.nosuch" });

        response.Matches.ShouldBeEmpty();
    }

    [Fact]
    public void Search_UnknownPrefix_StillThrowsSubtreeError()
    {
        var fixture = NewGlobFixture();
        var engine = new SearchEngine(fixture.Workspace);

        var ex = Should.Throw<WorkspaceException>(() =>
            engine.Search(GlobQuery("needle") with { Prefix = "missing/" }));

        ex.Message.ShouldContain("No indexed files under prefix");
    }

    [Fact]
    public async Task PathGlob_SemanticArmStaysInsideGlob_AndFillsTopK()
    {
        var fixture = NewGlobFixture();
        var engine = new SearchEngine(fixture.Workspace);
        var store = new FakeVectorStore();
        store.Seed(GlobFixtureChunks(fixture));

        var response = await engine.SearchHybridAsync(
            GlobQuery("zzquiescent watershed") with { PathGlob = "a/**" },
            TestVectorPhase.Arm(fixture.Workspace, new FakeEmbeddingProvider(), store),
                        CancellationToken.None);

        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldAllBe(m => m.Path == "a/x.js");
        response.Matches.ShouldContain(m => m.Source == "semantic");
        store.LastTopK.ShouldBe(ScopedVectorBudget.For(1, 2));
        store.LastTopK!.Value.ShouldBeGreaterThan(ScopedVectorBudget.BaseTopK);
    }

    private static SearchQuery GlobQuery(string text) => new(text, false, null, null, null, null, null, null);

    private static GlobFixture NewGlobFixture()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        const string source = """
            // lookup logic for the needle collector
            export function needleLookup() {
              return collector;
            }
            """;
        WriteFile(workspace, "repo/a/x.js", source);
        WriteFile(workspace, "repo/b/x.js", source);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var fileIds = meta.ListFiles(meta.FindRepo("demo")!.Id).ToDictionary(f => f.Path, f => f.Id);
        return new GlobFixture(workspace, fileIds);
    }

    private static IReadOnlyList<VectorHit> GlobFixtureChunks(GlobFixture fixture)
    {
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(fixture.Workspace));
        var repoId = meta.FindRepo("demo")!.Id;
        return fixture.FileIdsByPath
            .Select((entry, i) => (Entry: entry, Id: (ulong)(i + 1)))
            .Select(pair =>
            {
                var chunk = meta.ListChunksByFile(pair.Entry.Value).First();
                return new VectorHit(pair.Id, "h", pair.Entry.Value, repoId, chunk.StartLine, chunk.EndLine, 0.9 - pair.Entry.Key.Length * 0.01);
            })
            .ToList();
    }

    private sealed record GlobFixture(string Workspace, Dictionary<string, uint> FileIdsByPath);

    [Fact]
    public void Ranking_FilenameMatchBeatsBody_TestFilesAreDemoted()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths[0].ShouldBe("src/WorkspaceManager.cs");
        paths[^1].ShouldBe("src/WidgetTests.cs");
    }

    [Fact]
    public void TopK_LimitsResultCount()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, 2, null));

        response.Matches.Count.ShouldBe(2);
    }

    [Fact]
    public void MaxTokens_TruncatesWithinBudget()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, 15));

        response.Matches.ShouldHaveSingleItem();
        response.TokensUsed.ShouldBeLessThanOrEqualTo(15);
        response.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void Search_WithoutIndex_ThrowsFriendlyError()
    {
        var workspace = TestDirs.NewTempDir();

        Should.Throw<WorkspaceException>(() =>
            new SearchEngine(workspace).Search(new SearchQuery("anything", false, null, null, null, null, null, null)));
    }

    [Fact]
    public void Ranking_ContestsFileIsNotDemoted_TestsSuffixIs()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Contests.cs", $"class Contests {{ /* {FixtureContent} */ }}\n");
        WriteFile(workspace, "repo/src/FooTests.cs", $"class FooTests {{ /* {FixtureContent} */ }}\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var response = engine.Search(new SearchQuery(FixtureContent, false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths[0].ShouldBe("src/Contests.cs");
        paths[^1].ShouldBe("src/FooTests.cs");
        response.Matches.First(m => m.Path == "src/Contests.cs").Score
            .ShouldBeGreaterThan(response.Matches.First(m => m.Path == "src/FooTests.cs").Score);
    }

    [Fact]
    public void Snippet_LongLineIsTruncatedWithMarker()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var longLine = $"// {new string('x', 250)} {FixtureContent} {new string('y', 250)}";
        WriteFile(workspace, "repo/a.cs", longLine + "\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var match = engine.Search(new SearchQuery(FixtureContent, false, null, null, null, null, null, null))
            .Matches.ShouldHaveSingleItem();

        match.Snippet.ShouldEndWith("…");
        match.Snippet.Length.ShouldBe(201);
    }

    [Fact]
    public void MaxTokens_ExactSnippetIsNeverBudgetClipped()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var longLine = $"// {new string('x', 250)} {FixtureContent} {new string('y', 250)}";
        WriteFile(workspace, "repo/a.cs", longLine + "\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var response = engine.Search(new SearchQuery(FixtureContent, false, null, null, null, null, 8, 100));
        var match = response.Matches.ShouldHaveSingleItem();

        match.Snippet.ShouldBe("// " + new string('x', 197) + "…");
        response.TokensUsed.ShouldBe(TokenCounter.Estimate(match.Snippet));
        response.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Regex_InvalidPattern_ThrowsFriendlyError()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var ex = Should.Throw<WorkspaceException>(() =>
            engine.Search(new SearchQuery("[", true, null, null, null, null, null, null)));

        ex.Message.ShouldContain("Invalid regex pattern");
    }

    [Fact]
    public void Regex_CatastrophicBacktracking_SkipsCandidateWithoutCrashing()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/padded.cs", new string('a', 1500) + "X");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var response = engine.Search(new SearchQuery("(a+)+$", true, null, null, null, null, null, null));

        response.Matches.ShouldBeEmpty();
    }

    private static string NewIndexedWorkspace(bool addSecondRepo = false)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/WorkspaceManager.cs", "public class WorkspaceManager\n{\n}\n");
        WriteFile(workspace, "repo/src/worker.cs", $"// uses {FixtureContent} internally\nvar helper = \"{FixtureContent}\";\n");
        WriteFile(workspace, "repo/src/WidgetTests.cs", $"class WidgetTests {{ /* {FixtureContent} */ }}\n");
        WriteFile(workspace, "repo/lib/util.js", $"function {FixtureContent}Helper() {{ }}\n");
        WriteFile(workspace, "repo/docs/notes.md", $"notes about {FixtureContent}\n");
        manager.AddRepo("repo", "demo");

        if (addSecondRepo)
        {
            WriteFile(workspace, "other/clone.cs", $"class Clone {{ /* {FixtureContent} */ }}\n");
            manager.AddRepo("other", "other");
        }

        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.Calibrated;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => new[] { 0.25f, 0.5f, 0.75f, 1f }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        private IReadOnlyList<VectorHit> _hits = [];

        public int? LastTopK { get; private set; }

        public void Seed(IReadOnlyList<VectorHit> hits) => _hits = hits;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
        {
            LastTopK = topK;
            return Task.FromResult(_hits);
        }

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)_hits.Count);

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
