using System.Security.Cryptography;
using System.Text;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Shared plumbing for the per-language fixture end-to-end classes. RustFixtureEndToEndTests carries its
// own private copies of these helpers and stays untouched; the python and go classes are the third and
// fourth readers of the same shape, so they share one file instead of copying the two vector-store stubs
// twice more.
//
// The indexing contract is what keeps a fixture test off the live vector service: the workspace config
// points Qdrant at a dead endpoint and the indexer is handed the fake provider plus a recording store that
// swallows every upsert. Chunks therefore leave the run stamped "ok" with a model key — the embed-side
// status is a persisted fact the tests read back — while no collection is created or written anywhere.
internal static class TsFixtureHarness
{
    internal static string IndexedFixture(string fixtureDir, string twinRelativePath, string twinSource)
    {
        var workspace = TestDirs.NewTempDir();
        var repo = Path.Combine(workspace, "repo");
        Directory.CreateDirectory(repo);
        Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureDir), repo);
        Write(workspace, $"repo/{twinRelativePath}", twinSource);

        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(
            workspace, vectorPhase: TestVectorPhase.Runtime(new FakeEmbeddingProvider(), new RecordingVectorStore()));
        return workspace;
    }

    internal static void Copy(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    internal static void Write(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    internal static MetaStore OpenStore(string workspace) => new(SqueezrPaths.MetaDbPath(workspace));

    // import rows read across named files in path order: the store hands rows back unordered, and a
    // binding rule is only comparable against a statement an agent can locate
    internal static List<(string? Module, string Name, int Line)> ReadImports(
        MetaStore store, Dictionary<string, FileDocument> filesByPath, params string[] paths) =>
        paths
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => store.ListRefsByFile(filesByPath[path].Id).Where(r => r.Kind == "import")
                .OrderBy(r => r.Line)
                .ThenBy(r => r.ToName, StringComparer.Ordinal)
                .Select(r => (r.ToSymbolId, r.ToName, r.Line)))
            .ToList();

    internal static ParseResult ParseTwin(string relativePath, string source) =>
        new TreeSitterProvider(Squeezr.Semantic.Configuration.SemanticConfig.Bind(Squeezr.Core.Configuration.SqueezrConfig.CreateDefault()).Core)
            .Parse(new FileRef("demo", relativePath), Encoding.UTF8.GetBytes(source));

    internal sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.Calibrated;

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return new[] { bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f };
            }).ToArray());
    }

    // every write is a no-op by design: a fixture test that reached a real collection would be writing
    // into the machine's shared vector service, which a pinned SQUEEZR_HOME does not isolate
    internal sealed class RecordingVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

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

    internal sealed class OneHitVectorStore : IVectorStore
    {
        private readonly VectorHit _hit;

        public OneHitVectorStore(uint repoId, ChunkDocument chunk) =>
            _hit = new VectorHit(1, chunk.ChunkHash, chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.9);

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([_hit]);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(1L);

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
