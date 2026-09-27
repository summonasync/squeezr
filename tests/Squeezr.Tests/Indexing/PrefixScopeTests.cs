using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

[Collection("CliProcessGlobals")]
public class PrefixScopeTests
{
    private const string Marker = "sharedneedle";

    [Fact]
    public void Map_Prefix_RendersSubtreeRelativeToPrefix()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 2, RepoMapper.DefaultMaxTokens, "src/");

        result.Truncated.ShouldBeFalse();
        var texts = result.Lines.Select(l => l.Text).ToList();
        texts.ShouldContain("demo/");
        texts.ShouldContain(t => t.StartsWith("Core/", StringComparison.Ordinal));
        texts.ShouldContain(t => t.StartsWith("Cli/", StringComparison.Ordinal));
        texts.ShouldContain("Alpha.cs");
        texts.ShouldNotContain(t => t.Contains("util.js"));
        texts.ShouldNotContain(t => t.Contains("Guide.cs"));
        texts.ShouldNotContain(t => t.StartsWith("src/", StringComparison.Ordinal));
    }

    [Fact]
    public void Map_Prefix_DepthOne_ShowsOnlySubtreeDirs()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 1, RepoMapper.DefaultMaxTokens, "src/");

        result.Lines.ShouldAllBe(l => l.Depth <= 1);
        result.Lines.Where(l => l.Depth == 1).Select(l => l.Text)
            .ShouldBe(["Cli/ (1 files)", "Core/ (1 files)"]);
    }

    [Fact]
    public void Map_Prefix_DepthThree_MembersOnlyFromSubtree()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 3, RepoMapper.DefaultMaxTokens, "src/");

        var texts = result.Lines.Select(l => l.Text).ToList();
        texts.ShouldContain(t => t.Contains("Add(int a, int b)"));
        texts.ShouldNotContain(t => t.Contains("helper"));
    }

    [Fact]
    public void Map_Prefix_VariantSpellings_NormalizeIdentically()
    {
        var workspace = NewIndexedWorkspace();
        var mapper = new RepoMapper(workspace);
        var baseline = mapper.Build(null, 2, RepoMapper.DefaultMaxTokens, "src/")
            .Lines.Select(l => (l.Depth, l.Text)).ToList();

        foreach (var variant in new[] { "src", "/src/", "./src/", @"src\", "SRC/", " src " })
        {
            mapper.Build(null, 2, RepoMapper.DefaultMaxTokens, variant)
                .Lines.Select(l => (l.Depth, l.Text)).ShouldBe(baseline);
        }
    }

    [Fact]
    public void Map_AbsentOrEmptyPrefix_ByteIdenticalToBaseline()
    {
        var workspace = NewIndexedWorkspace();
        var mapper = new RepoMapper(workspace);
        var baseline = mapper.Build(null, 2, RepoMapper.DefaultMaxTokens);

        foreach (var variant in new string?[] { null, "", "   ", "/" })
        {
            var result = mapper.Build(null, 2, RepoMapper.DefaultMaxTokens, variant);
            result.Lines.Select(l => (l.Depth, l.Text))
                .ShouldBe(baseline.Lines.Select(l => (l.Depth, l.Text)));
            result.TokensUsed.ShouldBe(baseline.TokensUsed);
            result.Truncated.ShouldBe(baseline.Truncated);
        }
    }

    [Fact]
    public void Map_InvalidPrefix_ThrowsNamingNearestDirs()
    {
        var workspace = NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(() =>
            new RepoMapper(workspace).Build(null, 2, RepoMapper.DefaultMaxTokens, "libb/"));

        ex.Code.ShouldBe(WorkspaceException.InvalidQueryCode);
        ex.Message.ShouldContain("No indexed files under prefix 'libb/'");
        ex.Message.ShouldContain("lib/");
    }

    [Fact]
    public void Map_PrefixWithParentSegments_Rejected()
    {
        var workspace = NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(() =>
            new RepoMapper(workspace).Build(null, 2, RepoMapper.DefaultMaxTokens, "src/../docs"));

        ex.Code.ShouldBe(WorkspaceException.InvalidQueryCode);
        ex.Message.ShouldContain("'..' segments");
    }

    [Fact]
    public void Search_ExactArm_PrefixScopesMatches()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        engine.Search(Query(Marker))
            .Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["docs/Guide.cs", "lib/util.js", "src/Cli/Beta.cs", "src/Core/Alpha.cs"]);

        engine.Search(Query(Marker, "src/"))
            .Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["src/Cli/Beta.cs", "src/Core/Alpha.cs"]);
    }

    [Fact]
    public void Search_AbsentOrEmptyPrefix_MatchesUnscopedBaseline()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);
        var baseline = engine.Search(Query(Marker))
            .Matches.Select(m => (m.Path, m.Line)).ToList();

        foreach (var variant in new[] { "", "   ", "/" })
        {
            engine.Search(Query(Marker, variant))
                .Matches.Select(m => (m.Path, m.Line)).ShouldBe(baseline);
        }
    }

    [Fact]
    public void Search_InvalidPrefix_ThrowsNamingNearestDirs()
    {
        var workspace = NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(() =>
            new SearchEngine(workspace).Search(Query(Marker, "srcx/")));

        ex.Code.ShouldBe(WorkspaceException.InvalidQueryCode);
        ex.Message.ShouldContain("No indexed files under prefix 'srcx/'");
        ex.Message.ShouldContain("src/Cli/");
        ex.Message.ShouldContain("src/Core/");
    }

    [Fact]
    public void Search_PrefixWithParentSegments_Rejected()
    {
        var workspace = NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(() =>
            new SearchEngine(workspace).Search(Query(Marker, "../repo")));

        ex.Code.ShouldBe(WorkspaceException.InvalidQueryCode);
        ex.Message.ShouldContain("'..' segments");
    }

    [Fact]
    public void Search_Prefix_OnUnbuiltIndex_ReportsIndexNotBuilt()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init(null);

        var ex = Should.Throw<WorkspaceException>(() =>
            new SearchEngine(workspace).Search(Query(Marker, "src/")));

        ex.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
    }

    [Fact]
    public void Search_Prefix_CombinesWithRepoFilter()
    {
        var workspace = NewIndexedWorkspace();

        var scoped = new SearchEngine(workspace).Search(
            new SearchQuery(Marker, false, null, "demo", null, null, null, null, "src/"));

        scoped.Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["src/Cli/Beta.cs", "src/Core/Alpha.cs"]);
    }

    [Fact]
    public void Hybrid_Prefix_SemanticArmDropsOutOfScopeHits()
    {
        var workspace = NewIndexedWorkspace(withChunks: true);
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = meta.FindRepo("demo")!.Id;
        var srcFile = meta.ListFiles(repoId).First(f => f.Path == "src/Core/Alpha.cs");
        var libFile = meta.ListFiles(repoId).First(f => f.Path == "lib/util.js");
        var srcChunks = meta.ListChunksByFile(srcFile.Id);
        srcChunks.ShouldNotBeEmpty();
        var libChunks = meta.ListChunksByFile(libFile.Id);
        libChunks.ShouldNotBeEmpty();
        var srcChunk = srcChunks.First();
        var libChunk = libChunks.First();
        var store = new FakeVectorStore
        {
            Hits =
            [
                new VectorHit(1, "h1", libFile.Id, repoId, libChunk.StartLine, libChunk.EndLine, 0.9),
                new VectorHit(2, "h2", srcFile.Id, repoId, srcChunk.StartLine, srcChunk.EndLine, 0.8)
            ]
        };
        var engine = new SearchEngine(workspace);

        var scoped = Hybrid(engine, store, "src/");
        store.LastTopK.ShouldBe(80);
        scoped.Matches.ShouldHaveSingleItem().Path.ShouldBe("src/Core/Alpha.cs");

        var unscoped = Hybrid(engine, store, null);
        store.LastTopK.ShouldBe(20);
        unscoped.Matches.Select(m => m.Path)
            .ShouldBe(["lib/util.js", "src/Core/Alpha.cs"]);
    }

    [Fact]
    public async Task Cli_MapPrefix_RendersSubtree_AndInvalidPrefixErrors()
    {
        var workspace = NewIndexedWorkspace();

        var map = await RunCliAsync(workspace, "map", "--prefix", "src/", "--json");
        map.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(map.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
            var texts = doc.RootElement.GetProperty("lines").EnumerateArray()
                .Select(l => l.GetProperty("text").GetString()!).ToList();
            texts.ShouldContain("Cli/ (1 files)");
            texts.ShouldNotContain(t => t.Contains("util.js"));
        }

        var bad = await RunCliAsync(workspace, "map", "--prefix", "srcx/", "--json");
        bad.ExitCode.ShouldBe(1);
        bad.StdOut.ShouldBeEmpty();
        using (var doc = JsonDocument.Parse(bad.StdErr.Trim()))
        {
            var error = doc.RootElement.GetProperty("error");
            error.GetProperty("code").GetString().ShouldBe("invalid_query");
            error.GetProperty("message").GetString()!.ShouldContain("Nearest directories");
        }
    }

    [Fact]
    public async Task Cli_SearchPrefix_ScopesHits_AndInvalidPrefixErrors()
    {
        var workspace = NewIndexedWorkspace();

        var search = await RunCliAsync(workspace, "search", Marker, "--prefix", "src/", "--json");
        search.ExitCode.ShouldBe(0);
        var matches = ParseMatchLines(search.StdOut);
        matches.Select(m => m.GetProperty("path").GetString()!)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["src/Cli/Beta.cs", "src/Core/Alpha.cs"]);

        var bad = await RunCliAsync(workspace, "search", Marker, "--prefix", "nope/", "--json");
        bad.ExitCode.ShouldBe(1);
        bad.StdOut.ShouldBeEmpty();
        using (var doc = JsonDocument.Parse(bad.StdErr.Trim()))
        {
            doc.RootElement.GetProperty("error").GetProperty("code").GetString()
                .ShouldBe("invalid_query");
        }
    }

    private static SearchResponse Hybrid(SearchEngine engine, FakeVectorStore store, string? prefix) =>
        engine.SearchHybridAsync(
            Query("whatever", prefix),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();

    private static SearchQuery Query(string query, string? prefix = null) =>
        new(query, false, null, null, null, null, null, null, prefix);

    private static List<JsonElement> ParseMatchLines(string stdOut) =>
        stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line))
            .Select(d => d.RootElement.Clone())
            .Where(e => e.GetProperty("type").GetString() == "match")
            .ToList();

    private static string NewIndexedWorkspace(bool withChunks = false)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        if (withChunks)
        {
            TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
            TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        }

        WriteFile(workspace, "repo/src/Core/Alpha.cs", $$"""
            public class Alpha
            {
                // {{Marker}}
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Cli/Beta.cs", $$"""
            public class Beta
            {
                // {{Marker}}
                public int Run() => 1;
            }
            """);
        WriteFile(workspace, "repo/lib/util.js", $$"""
            function helper() {
                // {{Marker}}
                return 1;
            }
            """);
        WriteFile(workspace, "repo/docs/Guide.cs", $$"""
            public class Guide
            {
                // {{Marker}}
                public string Topic => "indexing";
            }
            """);
        manager.AddRepo("repo", "demo");

        if (withChunks)
        {
            new WorkspaceIndexer().Index(
                workspace,
                embeddings: new FakeEmbeddingProvider(),
                vectorStore: new FakeVectorStore());
        }
        else
        {
            new WorkspaceIndexer().Index(workspace);
        }

        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

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

        public int? LastTopK { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct)
        {
            LastTopK = topK;
            return Task.FromResult(Hits);
        }

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Hits.Count);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
