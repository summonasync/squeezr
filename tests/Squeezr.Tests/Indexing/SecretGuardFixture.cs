using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// A store built the way stores were built before the guard existed: the secret files are indexed,
// chunked and addressable, so every read surface has something to leak. Each protected file carries
// its own canary value — one query, one expected withholding.
internal static class SecretGuardFixture
{
    internal const string EnvPath = ".env";
    internal const string PemPath = "deploy/keys/monkey.pem";
    internal const string SecretsPath = "config/secrets.json";
    internal const string TokensPath = "notes/tokens.txt";
    internal const string ExamplePath = ".env.example";
    internal const string AppPath = "src/App.cs";

    internal const string EnvCanary = "SS1-CANARY-ENV-VALUE";
    internal const string PemCanary = "SS2-CANARY-PEM-VALUE";
    internal const string SecretsCanary = "SS3-CANARY-JSON-VALUE";
    internal const string TokensCanary = "SS4-CANARY-TOKENS-VALUE";
    internal const string ExampleCanary = "SS5-PUBLIC-EXAMPLE-VALUE";
    internal const string AppCanary = "SS6-APP-VALUE";

    internal static readonly string[] ProtectedCanaries = [EnvCanary, PemCanary, SecretsCanary, TokensCanary];

    internal static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");

        Write(workspace, AppPath, """
            public class App
            {
                public static string Greeting => "SS6-APP-VALUE";

                public void Run()
                {
                    Console.WriteLine(Greeting);
                }
            }
            """);
        Write(workspace, EnvPath, $"OPENAI_API_KEY={EnvCanary}\n");
        Write(workspace, ExamplePath, $"OPENAI_API_KEY={ExampleCanary}\n");
        Write(workspace, PemPath, $"{PemCanary}\n");
        Write(workspace, SecretsPath, $"{{ \"token\": \"{SecretsCanary}\" }}\n");
        Write(workspace, TokensPath, $"{TokensCanary}\n");

        new WorkspaceIndexer().Index(workspace);

        // Ingestion refuses the protected names now, so the pre-guard store has to be written the way a
        // pre-guard store was written by hand: rows and postings straight into the store, no pass involved.
        PlantPreGuardRows(workspace, [EnvPath, PemPath, SecretsPath]);
        return workspace;
    }

    internal static void PlantPreGuardRows(string workspace, IReadOnlyList<string> relativePaths)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.ListRepos().First();
        var repoRoot = Path.GetFullPath(
            Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspace, repo.Path));
        var chunking = ConfigFile.Load(workspace).Chunking;
        var postings = new List<TrigramDocument>();

        foreach (var relativePath in relativePaths)
        {
            var text = File.ReadAllText(
                Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var doc = new FileDocument
            {
                Id = store.GetNextFileId(),
                RepoId = repo.Id,
                Path = relativePath,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                Language = LanguageDetector.Detect(relativePath),
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
                LineCount = text.Count(c => c == '\n') + 1,
                IndexedAt = DateTime.UtcNow,
            };
            store.UpsertFile(doc);
            FallbackChunker.Persist(store, repo, doc, text, chunking);
            postings.Add(new TrigramDocument(
                doc.RepoId,
                doc.Id,
                TrigramExtractor.ExtractPostings(text).ToDictionary(kv => kv.Key, kv => kv.Value.ToArray())));
        }

        TrigramIndexWriter.AppendBatch(SqueezrPaths.TrigramIndexPath(workspace), postings, []);
    }

    private static DateTime _lastConfigStamp = DateTime.MinValue;

    // config writes can land inside one mtime tick on coarse filesystems, and these tests reason about the
    // cache generation that a config edit moves — so every edit gets a strictly newer stamp. Back-to-back
    // calls within a second step the stamp into the future relative to meta.db, which is exactly the
    // future-dated-config state; that is disclosed and safe since the cache evicts on a change of either
    // generation component, not on a max() blend — ToolResponseCacheTests pins it from both directions.
    internal static void SetGuardExtra(string workspace, params string[] globs)
    {
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with { Indexing = config.Indexing with { SecretGuardExtra = globs } });

        var now = DateTime.UtcNow;
        _lastConfigStamp = now.Ticks > _lastConfigStamp.Ticks
            ? now
            : _lastConfigStamp.AddSeconds(1);
        File.SetLastWriteTimeUtc(SqueezrPaths.ConfigPath(workspace), _lastConfigStamp);
    }

    // the guard reads its extensions from config.json on every call; a broken file must degrade loudly
    // rather than silently serving the user half of a mask
    internal static void CorruptConfig(string workspace, string content = "}{ not json") =>
        File.WriteAllText(SqueezrPaths.ConfigPath(workspace), content);

    internal static long PlantSymbol(
        string workspace,
        string relativePath,
        string name,
        string kind,
        string? docComment = null)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var file = RequireFile(store, relativePath);
        store.InsertSymbols(
        [
            new SymbolDocument
            {
                FileId = file.Id,
                RepoId = file.RepoId,
                Name = name,
                Kind = kind,
                Signature = $"{kind} {name}",
                StartLine = 1,
                EndLine = file.LineCount,
                DocComment = docComment,
            }
        ]);
        return store.ListSymbolsByFile(file.Id).First(s => s.Name == name).Id;
    }

    internal static void PlantReferenceFrom(string workspace, string symbolName, string relativePath, int line)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var source = store.ListSymbolsByRepo(RequireRepo(store).Id).First(s => s.Name == symbolName);
        var target = RequireFile(store, relativePath);
        store.InsertRefs(
        [
            new RefDocument
            {
                FromSymbolId = source.Id,
                ToName = source.Name,
                Kind = "reference",
                FileId = target.Id,
                Line = line,
                EnclosingSymbol = "App." + symbolName,
            }
        ]);
    }

    // chunk order out of the store is not stable (the semantic phase and the fallback chunker both write),
    // so a fixture picks the chunk that actually carries the text it wants surfaced or withheld
    internal static (uint FileId, uint RepoId, int StartLine, int EndLine) ChunkHolding(
        string workspace, string relativePath, string needle)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var file = RequireFile(store, relativePath);
        var chunk = store.ListChunksByFile(file.Id)
            .Where(c => c.Text.Contains(needle, StringComparison.Ordinal))
            .OrderBy(c => c.StartLine)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"no chunk of '{relativePath}' holds '{needle}'");
        return (file.Id, file.RepoId, chunk.StartLine, chunk.EndLine);
    }

    private static FileDocument RequireFile(MetaStore store, string relativePath) =>
        store.ListAllFiles().FirstOrDefault(f => f.Path == relativePath)
        ?? throw new InvalidOperationException($"'{relativePath}' is not in the fixture store");

    private static RepoDocument RequireRepo(MetaStore store) =>
        store.ListRepos().First();

    private static void Write(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, "repo", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    internal sealed class FixedEmbeddings : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => new[] { 0.1f, 0.2f, 0.3f, 0.4f }).ToArray());
    }

    internal sealed class ScriptedVectorStore : IVectorStore
    {
        public IReadOnlyList<VectorHit> Hits { get; init; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult(Hits);

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
