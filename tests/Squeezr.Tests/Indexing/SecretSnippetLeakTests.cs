using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// The byte-level proof of the redact-before-chunking policy: an appsettings-shaped file full of
// canaries goes through a real index pass and no canary byte may surface in a chunk row, an
// embedding input, a vector point, the meta store bytes or a search snippet — while the keys,
// the structure and the plain settings keep answering queries.
public class SecretSnippetLeakTests
{
    private const string ConnStringCanary = "SC1-CANARY-CONN-VALUE";
    private const string ApiKeyCanary = "SC2-CANARY-KEY-VALUE";
    private const string VaultCanary = "SC3-CANARY-VAULT-VALUE";
    private const string CustomCanary = "SC4-CANARY-CUSTOM-VALUE";

    private static readonly string[] Canaries = [ConnStringCanary, ApiKeyCanary, VaultCanary];

    [Fact]
    public void Index_FullPass_NeverStoresAConfigSecretValueOnAnySurface()
    {
        var workspace = NewWorkspaceWithAppSettings();
        var embeddings = new CapturingEmbeddings();
        var vectors = new CapturingVectorStore();

        new WorkspaceIndexer().Index(workspace, embeddings: embeddings, vectorStore: vectors);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var chunkTexts = ChunkTexts(store);
        chunkTexts.ShouldNotBeEmpty();
        embeddings.Inputs.ShouldNotBeEmpty();

        foreach (var canary in Canaries)
        {
            chunkTexts.ShouldNotContain(c => c.Contains(canary, StringComparison.Ordinal));
            embeddings.Inputs.ShouldNotContain(t => t.Contains(canary, StringComparison.Ordinal));
            ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(canary);
        }

        vectors.Points.ShouldNotBeEmpty();
        foreach (var canary in Canaries)
        {
            vectors.Points.ShouldNotContain(p => Rendered(p).Contains(canary, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Search_FindsStructureAndPlainValues_ButServesOnlyMaskedSnippets()
    {
        var workspace = NewWorkspaceWithAppSettings();
        new WorkspaceIndexer().Index(workspace);
        var engine = new SearchEngine(workspace);

        var plain = engine.Search(Literal("Information"));
        var hit = plain.Matches.ShouldHaveSingleItem();
        hit.Path.ShouldBe("appsettings.json");
        hit.Snippet.ShouldContain("Information");
        hit.Snippet.ShouldNotContain(ConnStringCanary);

        var secretKey = engine.Search(Literal("ApiKey"));
        var masked = secretKey.Matches.ShouldHaveSingleItem();
        masked.Snippet.ShouldContain("[redacted]");
        masked.Snippet.ShouldNotContain(ApiKeyCanary);

        foreach (var canary in Canaries)
        {
            var leaked = engine.Search(Literal(canary));
            leaked.Matches.ShouldBeEmpty();
            leaked.Warnings.ShouldNotContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Index_StoreCarriesBothMarkers_SoBorderlineCasesStayAuditable()
    {
        var workspace = NewWorkspaceWithAppSettings();
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var chunkTexts = ChunkTexts(store);

        chunkTexts.ShouldContain(c => c.Contains("[redacted]", StringComparison.Ordinal));
        chunkTexts.ShouldContain(c => c.Contains("[redacted?]", StringComparison.Ordinal));
    }

    [Fact]
    public void Index_SecretGuardExtra_ExtendsTheNoLeakSurfaceToAnUncoveredFile()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/deploy/custom.settings", $"Token={CustomCanary}\nPort=8080\n");
        SecretGuardFixture.SetGuardExtra(workspace, "**/custom.settings");

        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        store.ListAllFiles().ShouldNotContain(f => f.Path == "deploy/custom.settings");
        ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(CustomCanary);
    }

    [Fact]
    public void IndexFile_InlineReindex_MasksCoveredFilesToo()
    {
        var workspace = NewWorkspaceWithAppSettings();
        new WorkspaceIndexer().Index(workspace);
        const string editedCanary = "SC5-CANARY-EDITED-VALUE";
        WriteFile(workspace, "repo/appsettings.json",
            $$"""{ "ApiKey": "{{editedCanary}}", "Port": "9090" }""");

        new WorkspaceIndexer().IndexFile(workspace, "demo", "appsettings.json");

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var chunkTexts = ChunkTexts(store);
        chunkTexts.ShouldNotContain(c => c.Contains(editedCanary, StringComparison.Ordinal));
        chunkTexts.ShouldContain(c => c.Contains("[redacted]", StringComparison.Ordinal));
        chunkTexts.ShouldContain(c => c.Contains("9090", StringComparison.Ordinal));
        ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(editedCanary);
    }

    [Fact]
    public void Index_TrigramRebuildFromDisk_StaysLeakFree()
    {
        var workspace = NewWorkspaceWithAppSettings();
        new WorkspaceIndexer().Index(workspace);
        File.WriteAllText(SqueezrPaths.TrigramIndexPath(workspace), "not a trigram segment");

        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        foreach (var canary in Canaries)
        {
            engine.Search(Literal(canary)).Matches.ShouldBeEmpty();
        }

        engine.Search(Literal("Information")).Matches.ShouldNotBeEmpty();
        ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(ApiKeyCanary);
    }

    private static string NewWorkspace()
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

    private static string NewWorkspaceWithAppSettings()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/appsettings.json", """
            {
              "ConnectionStrings": {
                "Default": "Server=localhost;Database=orders;Password=SC1-CANARY-CONN-VALUE"
              },
              "ApiKey": "SC2-CANARY-KEY-VALUE",
              "Logging": {
                "LogLevel": {
                  "Default": "Information"
                }
              },
              "Vault": "address=https://vault.internal;token=SC3-CANARY-VAULT-VALUE",
              "AllowedHosts": "*",
              "Port": "8080"
            }
            """);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static List<string> ChunkTexts(MetaStore store) =>
        [.. store.ListAllFiles().SelectMany(f => store.ListChunksByFile(f.Id)).Select(c => c.Text)];

    private static string Rendered(VectorPoint point) =>
        $"{point.Id}|{point.ChunkHash}|{point.Kind}|{point.FileId}|{point.RepoId}|{point.StartLine}|{point.EndLine}";

    private static string ReadAscii(string path) =>
        System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));

    private static SearchQuery Literal(string text) =>
        new(text, false, null, null, null, null, null, null);

    private sealed class CapturingEmbeddings : IEmbeddingProvider
    {
        internal List<string> Inputs { get; } = [];

        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            Inputs.AddRange(texts);
            return Task.FromResult(texts.Select(_ => new[] { 0.1f, 0.2f, 0.3f, 0.4f }).ToArray());
        }
    }

    private sealed class CapturingVectorStore : IVectorStore
    {
        internal List<VectorPoint> Points { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            Points.AddRange(points);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Points.Count);

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
