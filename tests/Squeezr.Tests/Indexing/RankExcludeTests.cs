using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Mcp;

namespace Squeezr.Tests.Indexing;

[Collection("ProcessGlobals")]
public class RankExcludeTests
{
    private const string Marker = "rankneedle";

    private static readonly string[] BaselinePaths =
    [
        "audits/Note.md",
        "audits/deep/Nested.md",
        "docs/Guide.md",
        "src/Cli/Beta.cs",
        "src/Core/Alpha.cs"
    ];

    [Fact]
    public void Parse_ValidPatterns_AllStayActive()
    {
        var exclusion = RankExclusion.Parse(["audits/**", "**/*.md", "docs/legacy/**", @"src\generated\**"]);

        exclusion.Warnings.ShouldBeEmpty();
        exclusion.Patterns.Count.ShouldBe(4);
        exclusion.Matches("audits/deep/Note.md").ShouldBeTrue();
        exclusion.Matches("src/generated/Dto.cs").ShouldBeTrue();
        exclusion.Matches("src/Core/Alpha.cs").ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("   ", "is empty")]
    [InlineData("/etc/**", "relative to the repository root")]
    [InlineData("../sibling/**", "climb above the repository root")]
    [InlineData("**.md", "degrades to '*'")]
    [InlineData("src/a**b.cs", "degrades to '*'")]
    [InlineData("C:/repo/generated/**", "names a drive letter")]
    [InlineData(@"C:\repo\generated\**", "names a drive letter")]
    [InlineData("z:/**", "names a drive letter")]
    public void Parse_MalformedPattern_WarnsAndKeepsTheRest(string pattern, string reason)
    {
        var exclusion = RankExclusion.Parse(["audits/**", pattern]);

        exclusion.Patterns.ShouldBe(["audits/**"]);
        exclusion.IsActive.ShouldBeTrue();
        var warning = exclusion.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain($"pattern '{pattern}'");
        warning.ShouldContain(reason);
        warning.ShouldContain("pattern ignored");
        exclusion.Matches("audits/Note.md").ShouldBeTrue();
    }

    [Fact]
    public void Parse_NoPatterns_IsTheSharedNoneInstance()
    {
        RankExclusion.Parse([]).ShouldBeSameAs(RankExclusion.None);
        RankExclusion.Parse(null).ShouldBeSameAs(RankExclusion.None);
        RankExclusion.None.IsActive.ShouldBeFalse();
        RankExclusion.None.Matches("audits/Note.md").ShouldBeFalse();
    }

    [Fact]
    public void Load_MissingOrCorruptConfig_LeavesRankingUncurated()
    {
        RankExclusion.Load(TestDirs.NewTempDir()).ShouldBeSameAs(RankExclusion.None);

        var workspace = NewIndexedWorkspace();
        File.WriteAllText(SqueezrPaths.ConfigPath(workspace), "{ not json");

        RankExclusion.Load(workspace).ShouldBeSameAs(RankExclusion.None);
    }

    [Fact]
    public void ConfigWrittenBeforeThisKey_StillLoadsAndSearchesUncurated()
    {
        var workspace = NewIndexedWorkspace();
        var config = JsonNode.Parse(File.ReadAllText(SqueezrPaths.ConfigPath(workspace)))!.AsObject();
        config["indexing"]!.AsObject().Remove("rankExclude");
        File.WriteAllText(SqueezrPaths.ConfigPath(workspace), config.ToJsonString());

        ConfigFile.Load(workspace).Indexing.RankExclude.ShouldBeEmpty();
        RankExclusion.Load(workspace).ShouldBeSameAs(RankExclusion.None);
        Paths(new SearchEngine(workspace, RankExclusion.Load(workspace))).ShouldBe(BaselinePaths);
    }

    [Fact]
    public void Search_ExcludedTree_DropsOutOfTheExactArm()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "audits/**");
        var engine = new SearchEngine(workspace, RankExclusion.Load(workspace));

        Paths(engine).ShouldBe(["docs/Guide.md", "src/Cli/Beta.cs", "src/Core/Alpha.cs"]);
    }

    [Fact]
    public void Search_EmptyRankExclude_ByteIdenticalToBaseline()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace);
        var baseline = Detail(new SearchEngine(workspace));

        Detail(new SearchEngine(workspace, RankExclusion.Load(workspace))).ShouldBe(baseline);
        Detail(new SearchEngine(workspace, RankExclusion.Parse([]))).ShouldBe(baseline);
        Detail(new SearchEngine(workspace, RankExclusion.None)).ShouldBe(baseline);
        Paths(new SearchEngine(workspace, RankExclusion.Load(workspace))).ShouldBe(BaselinePaths);
    }

    [Fact]
    public void Search_PrefixAndRankExclude_NarrowInAndPushOut()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "src/Cli/**", "docs/**");
        var engine = new SearchEngine(workspace, RankExclusion.Load(workspace));

        Paths(engine, "src/").ShouldBe(["src/Core/Alpha.cs"]);
    }

    [Fact]
    public void Search_PrefixWhoseWholeSubtreeIsExcluded_NoHitsNotAnError()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "docs/**");
        var engine = new SearchEngine(workspace, RankExclusion.Load(workspace));

        Paths(engine, "docs/").ShouldBeEmpty();
    }

    [Fact]
    public void Search_InvalidPrefix_StillErrorsWithExclusionActive()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "audits/**");

        var ex = Should.Throw<WorkspaceException>(() =>
            Paths(new SearchEngine(workspace, RankExclusion.Load(workspace)), "srcx/"));

        ex.Code.ShouldBe(WorkspaceException.InvalidQueryCode);
        ex.Message.ShouldContain("No indexed files under prefix 'srcx/'");
    }

    [Fact]
    public void Search_ExclusionCombinedWithRepoFilter()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "**/*.md");

        var matches = new SearchEngine(workspace, RankExclusion.Load(workspace))
            .Search(new SearchQuery(Marker, false, null, "demo", null, null, null, null, null))
            .Matches;

        matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["src/Cli/Beta.cs", "src/Core/Alpha.cs"]);
    }

    [Fact]
    public void ExcludedFile_StaysIndexedAndNavigable()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "audits/**");
        var engine = new SearchEngine(workspace, RankExclusion.Load(workspace));

        Paths(engine).ShouldNotContain("audits/Note.md");

        var expanded = new SpanExpander(workspace).Expand("audits/Note.md", 3, 3, 0, null, 1000);
        string.Join("\n", expanded.Span).ShouldContain(Marker);

        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = meta.FindRepo("demo")!.Id;
        meta.ListFiles(repoId).Select(f => f.Path).ShouldContain("audits/Note.md");
    }

    [Fact]
    public void Hybrid_ExcludedTree_DropsOutOfTheSemanticArm()
    {
        var workspace = NewIndexedWorkspace(withChunks: true);
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = meta.FindRepo("demo")!.Id;
        var note = meta.ListFiles(repoId).First(f => f.Path == "audits/Note.md");
        var alpha = meta.ListFiles(repoId).First(f => f.Path == "src/Core/Alpha.cs");
        var noteChunk = meta.ListChunksByFile(note.Id).First();
        var alphaChunk = meta.ListChunksByFile(alpha.Id).First();
        var store = new FakeVectorStore
        {
            Hits =
            [
                new VectorHit(1, "h1", note.Id, repoId, noteChunk.StartLine, noteChunk.EndLine, 0.95),
                new VectorHit(2, "h2", alpha.Id, repoId, alphaChunk.StartLine, alphaChunk.EndLine, 0.8)
            ]
        };
        TestWorkspaceConfig.SetRankExclude(workspace, "audits/**");
        var engine = new SearchEngine(workspace, RankExclusion.Load(workspace));

        var curated = Hybrid(engine, store);
        store.LastTopK.ShouldBe(ScopedVectorBudget.For(3, 5));
        store.LastTopK.ShouldBe(34);
        curated.Matches.Select(m => m.Path).ShouldNotContain("audits/Note.md");
        curated.Matches.ShouldContain(m => m.Path == "src/Core/Alpha.cs");
    }

    [Fact]
    public void Hybrid_EmptyRankExclude_KeepsTheUnscopedVectorTopK()
    {
        var workspace = NewIndexedWorkspace(withChunks: true);
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = meta.FindRepo("demo")!.Id;
        var note = meta.ListFiles(repoId).First(f => f.Path == "audits/Note.md");
        var alpha = meta.ListFiles(repoId).First(f => f.Path == "src/Core/Alpha.cs");
        var noteChunk = meta.ListChunksByFile(note.Id).First();
        var alphaChunk = meta.ListChunksByFile(alpha.Id).First();
        var store = new FakeVectorStore
        {
            Hits =
            [
                new VectorHit(1, "h1", note.Id, repoId, noteChunk.StartLine, noteChunk.EndLine, 0.95),
                new VectorHit(2, "h2", alpha.Id, repoId, alphaChunk.StartLine, alphaChunk.EndLine, 0.8)
            ]
        };
        TestWorkspaceConfig.SetRankExclude(workspace);
        var engine = new SearchEngine(workspace, RankExclusion.Load(workspace));

        var uncurated = Hybrid(engine, store);
        store.LastTopK.ShouldBe(20);
        uncurated.Matches.Select(m => m.Path).ShouldContain("audits/Note.md");
    }

    [Fact]
    public async Task Cli_Search_ReadsRankExcludeFromConfig_AndWarnsOnMalformedGlob()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "audits/**", "**.md");

        var run = await CliTestProcess.RunAsync(workspace, "search", Marker, "--json");

        run.ExitCode.ShouldBe(0);
        ParseMatchLines(run.StdOut).Select(m => m.GetProperty("path").GetString()!)
            .ShouldBe(["docs/Guide.md", "src/Cli/Beta.cs", "src/Core/Alpha.cs"]);
        run.StdErr.ShouldContain("indexing.rankExclude pattern '**.md'");
        run.StdErr.ShouldContain("pattern ignored");
    }

    [Fact]
    public async Task Mcp_IndexStatus_ListsActivePatterns_AndCodeSearchHonoursThem()
    {
        var workspace = NewIndexedWorkspace();
        TestWorkspaceConfig.SetRankExclude(workspace, "audits/**", "docs/**");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var status = Envelope(await harness.Client.CallToolAsync(
            "index_status", cancellationToken: CancellationToken.None));
        var warnings = status.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ToList();
        warnings.ShouldContain(w => w.Contains("indexing.rankExclude is active"));
        var curation = warnings.First(w => w.Contains("rankExclude"));
        curation.ShouldContain("'audits/**'");
        curation.ShouldContain("'docs/**'");
        curation.ShouldContain("expand_span");

        var search = Envelope(await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Marker },
            cancellationToken: CancellationToken.None));
        search.GetProperty("data").EnumerateArray()
            .Select(hit => hit.GetProperty("file").GetString()!)
            .ShouldBe(["src/Cli/Beta.cs", "src/Core/Alpha.cs"]);

        var expand = Envelope(await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "audits/Note.md", ["startLine"] = 3 },
            cancellationToken: CancellationToken.None));
        expand.GetProperty("data").GetProperty("span").EnumerateArray()
            .Select(l => l.GetString()!)
            .ShouldContain(l => l.Contains(Marker));
    }

    private static SearchResponse Hybrid(SearchEngine engine, FakeVectorStore store) =>
        engine.SearchHybridAsync(
            new SearchQuery("whatever", false, null, null, null, null, null, 2000, null),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();

    private static List<string> Paths(SearchEngine engine, string? prefix = null) =>
        engine.Search(Query(prefix)).Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private static List<(string Path, int Line, double Score, string Source)> Detail(SearchEngine engine) =>
        engine.Search(Query(null)).Matches.Select(m => (m.Path, m.Line, m.Score, m.Source)).ToList();

    private static SearchQuery Query(string? prefix) =>
        new(Marker, false, null, null, null, null, null, null, prefix);

    private static JsonElement Envelope(CallToolResult result)
    {
        result.IsError.ShouldNotBe(true);
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

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
        WriteFile(workspace, "repo/docs/Guide.md", $$"""
            # Guide

            {{Marker}} in a document that stays in the ranking.
            """);
        WriteFile(workspace, "repo/audits/Note.md", $$"""
            # Audit note

            {{Marker}} in an audit note nobody wants ranked above source.
            """);
        WriteFile(workspace, "repo/audits/deep/Nested.md", $$"""
            # Nested

            {{Marker}} one level deeper.
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

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
        {
            LastTopK = topK;
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
