using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class IdentifierTokenLegTests
{
    [Fact]
    public void Tokenize_SplitsOnNonAlnum_DropsShortAndPureDigit()
    {
        var tokens = IdentifierTokenLeg.Tokenize("workspace registry orphan husk lossless adoption repair store dirs");
        tokens.ShouldBe([
            "workspace", "registry", "orphan", "husk",
            "lossless", "adoption", "repair", "store", "dirs"
        ]);
    }

    [Fact]
    public void Tokenize_DropsPureDigitTokens()
    {
        var tokens = IdentifierTokenLeg.Tokenize("foo 123 bar 4567 baz");
        tokens.ShouldBe(["foo", "bar", "baz"]);
    }

    [Fact]
    public void Tokenize_DropsTokensShorterThan3()
    {
        var tokens = IdentifierTokenLeg.Tokenize("a bb ccc dd eeee");
        tokens.ShouldBe(["ccc", "eeee"]);
    }

    [Fact]
    public async Task Hybrid_MultiTokenQuery_NoPhraseMatch_ProducesExactHits()
    {
        var workspace = NewIndexedWorkspaceWithMultipleTokens();

        var engine = new SearchEngine(workspace);
        var response = await engine.SearchHybridAsync(
            new SearchQuery(
                "OrphanRegistry HuskLossless AdoptionRepair",
                false, null, null, null, null, null, null),
            new FakeEmbeddingProvider(),
            new FakeVectorStore(),
            CancellationToken.None);

        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldContain(m => m.Source == "exact");
        response.Matches.ShouldAllBe(m => m.AlsoMatched == 0);
    }

    [Fact]
    public void IdentifierHits_AloneInTheirFile_CarryNoAlsoMatched()
    {
        var workspace = NewIndexedWorkspaceWithMultipleTokens();

        var hits = IdentifierTokenLeg.Execute(workspace, Query("OrphanRegistry HuskLossless AdoptionRepair"), null, Guard(workspace));

        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(hit => hit.Match.AlsoMatched == 0);
    }

    [Fact]
    public async Task Hybrid_SingleTokenQuery_OutputEqualsExactOnlyArm()
    {
        var workspace = NewIndexedWorkspaceWithCaseSplit();
        var engine = new SearchEngine(workspace);
        var query = new SearchQuery("calc;", false, null, null, null, null, null, null);

        var exactOnly = engine.Search(query);
        var hybrid = await engine.SearchHybridAsync(
            query, new FakeEmbeddingProvider(), new FakeVectorStore(), CancellationToken.None);

        hybrid.Matches.ShouldNotBeEmpty();
        Render(hybrid).ShouldBe(Render(exactOnly));
        hybrid.Matches.ShouldAllBe(m => m.AlsoMatched == 0);
    }

    [Fact]
    public void TokensWithNoCandidates_AreNotSelectedAsDiscriminants()
    {
        var workspace = NewIndexedWorkspaceWithMultipleTokens();

        var counter = new IdentifierTokenLeg.ReadCounter();
        IdentifierTokenLeg.Execute(workspace, Query("quantum fluxbridge OrphanRegistry"), null, counter, Guard(workspace));

        counter.TokensSelected.ShouldBe(1);
    }

    [Fact]
    public async Task Hybrid_LowercaseQuery_FindsPascalCaseIdentifier()
    {
        var workspace = NewIndexedWorkspaceWithPascalCaseWatcher();

        var engine = new SearchEngine(workspace);
        var response = await engine.SearchHybridAsync(
            new SearchQuery("workspace watcher", false, null, null, null, null, null, null),
            new FakeEmbeddingProvider(),
            new FakeVectorStore(),
            CancellationToken.None);

        response.Matches.ShouldContain(m => m.Source == "exact" && m.Path == "WorkspaceWatcher.cs");
    }

    [Fact]
    public void CodeShapedToken_PassesDfCensor()
    {
        InvokeIsCodeShaped("MapGet").ShouldBeTrue();
        InvokeIsCodeShaped("some_Method").ShouldBeTrue();
        InvokeIsCodeShaped("LongEnoughIdentifier").ShouldBeTrue();
    }

    [Fact]
    public void CommonWordToken_FailsCodeShapedCheck()
    {
        InvokeIsCodeShaped("workspace").ShouldBeFalse();
        InvokeIsCodeShaped("the").ShouldBeFalse();
    }

    [Fact]
    public async Task Dedup_PhraseAndIdentifierSameLocation_OnlyPhraseWins()
    {
        var workspace = NewIndexedWorkspaceWithOverlap();

        var engine = new SearchEngine(workspace);
        var response = await engine.SearchHybridAsync(
            new SearchQuery("int Add", false, null, null, null, null, null, null),
            new FakeEmbeddingProvider(),
            new FakeVectorStore(),
            CancellationToken.None);

        var calcHits = response.Matches.Where(m => m.Path == "Calc.cs").ToList();
        calcHits.Count.ShouldBe(1);
        calcHits[0].AlsoMatched.ShouldBe(0);
    }

    [Fact]
    public void Caps_TwentyTokenQuery_DoesNotExceedEightTokensSelected()
    {
        var workspace = NewIndexedWorkspaceWithManyFiles();
        var query = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"Marker{i}Token"));

        var counter = new IdentifierTokenLeg.ReadCounter();
        IdentifierTokenLeg.Execute(workspace, Query(query), null, counter, Guard(workspace));

        counter.TokensSelected.ShouldBe(8);
        counter.CandidateReads.ShouldBeLessThanOrEqualTo(8 * 80);
        counter.MaxReadsPerFile.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Caps_ManyCandidatesPerToken_LimitedTo80()
    {
        var workspace = NewIndexedWorkspaceWithManyFiles();

        var counter = new IdentifierTokenLeg.ReadCounter();
        IdentifierTokenLeg.Execute(workspace, Query("WorkspaceManager WorkspaceManagerKeeper"), null, counter, Guard(workspace));

        counter.TokensSelected.ShouldBe(2);
        counter.CandidateReads.ShouldBe(160);
        counter.FileReads.ShouldBe(80);
        counter.MaxReadsPerFile.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Leg_WithoutFilters_ReachesEveryRepoAndSurface()
    {
        var workspace = NewIndexedWorkspaceWithFilterSurfaces();

        var hits = IdentifierTokenLeg.Execute(workspace, Query("OrphanRegistry HuskAdoption"), null, Guard(workspace));

        hits.Select(hit => hit.Match.Path).ToList()
            .ShouldBe(["generated/ForeignSubtree.cs", "lib/ForeignLang.js", "src/ForeignRepo.cs", "src/Keep.cs"]);
    }

    [Fact]
    public void Leg_RepoFilter_DropsForeignRepoCandidates()
    {
        var workspace = NewIndexedWorkspaceWithFilterSurfaces();

        var hits = IdentifierTokenLeg.Execute(
            workspace,
            Query("OrphanRegistry HuskAdoption") with { Repo = "demo" },
            null,
            Guard(workspace));

        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(hit => hit.Match.Repo == "demo");
        hits.ShouldNotContain(hit => hit.Match.Path == "src/ForeignRepo.cs");
    }

    [Fact]
    public void Leg_LanguageFilter_DropsForeignLanguageCandidates()
    {
        var workspace = NewIndexedWorkspaceWithFilterSurfaces();

        var hits = IdentifierTokenLeg.Execute(
            workspace,
            Query("OrphanRegistry HuskAdoption") with { Language = "csharp" },
            null,
            Guard(workspace));

        hits.ShouldNotBeEmpty();
        hits.ShouldNotContain(hit => hit.Match.Path == "lib/ForeignLang.js");
    }

    [Fact]
    public void Leg_PathGlobFilter_DropsForeignSubtreeCandidates()
    {
        var workspace = NewIndexedWorkspaceWithFilterSurfaces();

        var hits = IdentifierTokenLeg.Execute(
            workspace,
            Query("OrphanRegistry HuskAdoption") with { PathGlob = "**/src/**" },
            null,
            Guard(workspace));

        hits.ShouldNotBeEmpty();
        hits.ShouldNotContain(hit => hit.Match.Path.StartsWith("generated/", StringComparison.Ordinal));
        hits.ShouldNotContain(hit => hit.Match.Path.StartsWith("lib/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hybrid_RepoFilter_KeepsIdentifierLegInRepo()
    {
        var workspace = NewIndexedWorkspaceWithFilterSurfaces();
        var engine = new SearchEngine(workspace);

        var response = await engine.SearchHybridAsync(
            Query("OrphanRegistry HuskAdoption") with { Repo = "other" },
            new FakeEmbeddingProvider(),
            new FakeVectorStore(),
            CancellationToken.None);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Repo.ShouldBe("other");
        hit.Path.ShouldBe("src/ForeignRepo.cs");
        hit.Source.ShouldBe("exact");
    }

    [Fact]
    public void LongLowercaseToken_PastCaseVariantCap_CannotReachPascalOnlySpelling()
    {
        var workspace = NewIndexedWorkspaceWithLongIdentifier();
        var counter = new IdentifierTokenLeg.ReadCounter();

        var hits = IdentifierTokenLeg.Execute(workspace, Query("materialization note"), null, counter, Guard(workspace));

        // 13 trigrams leave a budget of 64/13 = 4 variants while the expansion needs 8, so the
        // token keeps its literal case, finds no candidates and drops out of the discriminants
        counter.TokensSelected.ShouldBe(1);
        hits.ShouldHaveSingleItem().Match.Path.ShouldBe("NoteVault.cs");
    }

    [Fact]
    public void ShortLowercaseToken_WithinCaseVariantCap_ReachesPascalOnlySpelling()
    {
        var workspace = NewIndexedWorkspaceWithLongIdentifier();
        var counter = new IdentifierTokenLeg.ReadCounter();

        var hits = IdentifierTokenLeg.Execute(workspace, Query("ledger note"), null, counter, Guard(workspace));

        counter.TokensSelected.ShouldBe(2);
        hits.ShouldContain(hit => hit.Match.Path == "MaterializationLedger.cs");
        hits.ShouldContain(hit => hit.Match.Path == "NoteVault.cs");
    }

    private static bool InvokeIsCodeShaped(string token)
    {
        var method = typeof(IdentifierTokenLeg).GetMethod(
            "IsCodeShaped",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        return (bool)method!.Invoke(null, [token])!;
    }

    [Fact]
    public async Task Hybrid_PrefixAndRepo_NarrowTheIdentifierArmTogether()
    {
        var workspace = NewTwoRepoWorkspace();
        var engine = new SearchEngine(workspace);

        var response = await engine.SearchHybridAsync(
            new SearchQuery("ZephyrGadget QuaternionGizmo", false, null, "alpha", null, null, 50, null, "src/"),
            new FakeEmbeddingProvider(),
            new FakeVectorStore(),
            CancellationToken.None);

        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldContain(m => m.Source == "exact");
        response.Matches.ShouldAllBe(m => m.Repo == "alpha" && m.Path.StartsWith("src/"));
    }

    [Fact]
    public async Task Hybrid_PrefixRepoAndLanguage_NarrowTheIdentifierArmTogether()
    {
        var workspace = NewTwoRepoWorkspace();
        var engine = new SearchEngine(workspace);

        var response = await engine.SearchHybridAsync(
            new SearchQuery("ZephyrGadget QuaternionGizmo", false, null, "alpha", null, "csharp", 50, null, "src/"),
            new FakeEmbeddingProvider(),
            new FakeVectorStore(),
            CancellationToken.None);

        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldAllBe(m => m.Repo == "alpha" && m.Path.StartsWith("src/"));
        response.Matches.ShouldAllBe(m => m.Path.EndsWith(".cs"), "language filter must reach the identifier arm");
    }

    [Fact]
    public async Task Hybrid_UnknownRepo_IdentifierArmAgreesWithPhraseArm()
    {
        var workspace = NewTwoRepoWorkspace();
        var engine = new SearchEngine(workspace);
        var query = new SearchQuery("ZephyrGadget QuaternionGizmo", false, null, "nosuchrepo", null, null, 50, null, null);

        engine.Search(query).Matches.ShouldBeEmpty();
        var response = await engine.SearchHybridAsync(
            query, new FakeEmbeddingProvider(), new FakeVectorStore(), CancellationToken.None);

        response.Matches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hybrid_RepoNameInAnotherCase_ResolvesLikeTheExactArm()
    {
        var workspace = NewTwoRepoWorkspace();
        var engine = new SearchEngine(workspace);

        var lower = await engine.SearchHybridAsync(
            new SearchQuery("ZephyrGadget QuaternionGizmo", false, null, "alpha", null, null, 50, null, null),
            new FakeEmbeddingProvider(), new FakeVectorStore(), CancellationToken.None);
        var upper = await engine.SearchHybridAsync(
            new SearchQuery("ZephyrGadget QuaternionGizmo", false, null, "ALPHA", null, null, 50, null, null),
            new FakeEmbeddingProvider(), new FakeVectorStore(), CancellationToken.None);

        lower.Matches.ShouldNotBeEmpty();
        Render(upper).ShouldBe(Render(lower),
            "repo names are case-insensitive in every arm, not only where the file list is filtered");

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        store.FindRepo("ALPHA").ShouldNotBeNull(
            "LiteDB name match is already case-insensitive; the rule is pinned here so no arm can tighten it later");
    }

    private static string NewTwoRepoWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);

        foreach (var (name, dir) in new[] { ("alpha", "repo-alpha"), ("beta", "repo-beta") })
        {
            var repoDir = Path.Combine(workspace, dir);
            Directory.CreateDirectory(Path.Combine(repoDir, "src"));
            Directory.CreateDirectory(Path.Combine(repoDir, "other"));
            manager.AddRepo(dir, name);
        }

        var source = """
            public class ZephyrGadget
            {
                public int QuaternionGizmo() => 1;
            }
            """;

        foreach (var dir in new[] { "repo-alpha", "repo-beta" })
        {
            File.WriteAllText(Path.Combine(workspace, dir, "src", "Widgets.cs"), source);
            File.WriteAllText(Path.Combine(workspace, dir, "src", "Widgets.js"), "const zephyrGadget = { quaternionGizmo: 1 };");
            File.WriteAllText(Path.Combine(workspace, dir, "other", "Loose.cs"), source);
        }

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithMultipleTokens()
    {
        var workspace = NewRepoWorkspace();

        var source = """
            namespace Storage;
            public class OrphanRegistry
            {
                public void Register() { }
            }
            public class HuskLossless
            {
                public void Process() { }
            }
            public class AdoptionRepair
            {
                public void Fix() { }
            }
            """;

        File.WriteAllText(Path.Combine(workspace, "repo", "Store.cs"), source);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithOverlap()
    {
        var workspace = NewRepoWorkspace();

        var source = """
            class Calc
            {
                int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        File.WriteAllText(Path.Combine(workspace, "repo", "Calc.cs"), source);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithManyFiles()
    {
        var workspace = NewRepoWorkspace();

        for (var i = 0; i < 100; i++)
        {
            var source = "namespace Repo;\n" +
                $"public class Class{i}\n{{\n" +
                $"    public void Method{i}() {{ }}\n" +
                $"    public string WorkspaceManager{i} = \"test\";\n" +
                "    public const string WorkspaceManagerKeeper = \"keeper\";\n" +
                (i < 20 ? $"    public const string Marker{i}Token = \"marker\";\n" : string.Empty);
            File.WriteAllText(Path.Combine(workspace, "repo", $"File{i}.cs"), source);
        }

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithCaseSplit()
    {
        var workspace = NewRepoWorkspace();
        var source = """
            class Calc
            {
                // calc; the rounding note
                int Add(int a, int b) => a + b;
            }
            """;

        File.WriteAllText(Path.Combine(workspace, "repo", "Split.cs"), source);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithPascalCaseWatcher()
    {
        var workspace = NewRepoWorkspace();
        var source = """
            namespace Watching;

            public class WorkspaceWatcher
            {
                public void WatchWorkspace() { }
            }
            """;

        File.WriteAllText(Path.Combine(workspace, "repo", "WorkspaceWatcher.cs"), source);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithFilterSurfaces()
    {
        const string csharpSource = """
            namespace Surfaces;

            public class OrphanRegistry
            {
                public HuskAdoption Track() => new();
            }

            public sealed class HuskAdoption;
            """;

        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);

        WriteFile(workspace, "repo/src/Keep.cs", csharpSource);
        WriteFile(workspace, "repo/generated/ForeignSubtree.cs", csharpSource);
        WriteFile(workspace, "repo/lib/ForeignLang.js", """
            export class OrphanRegistry {
              track() { return new HuskAdoption(); }
            }
            export class HuskAdoption {}
            """);
        WriteFile(workspace, "other/src/ForeignRepo.cs", csharpSource);

        manager.AddRepo("repo", "demo");
        manager.AddRepo("other", "other");
        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static string NewIndexedWorkspaceWithLongIdentifier()
    {
        var workspace = NewRepoWorkspace();

        WriteFile(workspace, "repo/MaterializationLedger.cs", """
            namespace Folding;

            public class MaterializationLedger
            {
                public int Count { get; set; }
            }
            """);
        WriteFile(workspace, "repo/NoteVault.cs", """
            namespace Folding;

            public class NoteVault
            {
                public int Count { get; set; }
            }
            """);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static SearchQuery Query(string text) =>
        new(text, false, null, null, null, null, null, null);

    private static SecretGuardReader Guard(string workspace) => SecretGuardReader.Load(workspace);

    private static string NewRepoWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static string Render(SearchResponse response) =>
        string.Join('\n', response.Matches.Select(m => $"{m.Repo}|{m.Path}|{m.Line}|{m.Source}|{m.AlsoMatched}|{m.Snippet}"))
        + $"|tokens={response.TokensUsed}";

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
        public bool Available { get; set; } = true;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
