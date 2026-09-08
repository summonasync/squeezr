using LiteDB;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public sealed class MetaStore : IDisposable
{
    public const string ReposCollection = "repos";
    public const string FilesCollection = "files";
    public const string SymbolsCollection = "symbols";
    public const string RefsCollection = "refs";
    public const string ChunksCollection = "chunks";
    public const string IndexStateCollection = "index_state";

    private readonly LiteDatabase _db;

    private static readonly object MapperWarmupLock = new();

    public MetaStore(string dbPath)
    {
        // LiteDB 5.0.21 exposes a half-built EntityMapper to concurrent readers (empty mapper is cached
        // before its members are populated), so first-time mapping of an entity type must be serialized.
        lock (MapperWarmupLock)
        {
            _db = new LiteDatabase(dbPath);
            _db.Mapper.ToDocument(new RepoDocument());
            _db.Mapper.ToDocument(new FileDocument());
            _db.Mapper.ToDocument(new IndexStateDocument { Stats = new IndexStateStats() });
            _db.Mapper.ToDocument(new SymbolDocument());
            _db.Mapper.ToDocument(new RefDocument());
            _db.Mapper.ToDocument(new ChunkDocument());
        }
        _db.GetCollection(ReposCollection).EnsureIndex("name", unique: true);
        _db.GetCollection(FilesCollection).EnsureIndex("repoId");
        _db.GetCollection(SymbolsCollection).EnsureIndex("fileId");
        _db.GetCollection(SymbolsCollection).EnsureIndex("repoId");
        _db.GetCollection(RefsCollection).EnsureIndex("fromSymbolId");
        _db.GetCollection(RefsCollection).EnsureIndex("fileId");
        _db.GetCollection(ChunksCollection).EnsureIndex("fileId");
        _db.GetCollection(ChunksCollection).EnsureIndex("repoId");
    }

    public void AddRepo(RepoDocument repo)
    {
        var repos = _db.GetCollection<RepoDocument>(ReposCollection);
        if (repos.Exists(r => r.Name == repo.Name))
        {
            throw new WorkspaceException($"Repository '{repo.Name}' is already present in the index store.");
        }
        repos.Insert(repo);
    }

    public void UpsertRepo(RepoDocument repo) => _db.GetCollection<RepoDocument>(ReposCollection).Upsert(repo);

    public RepoDocument? FindRepo(string name) =>
        _db.GetCollection<RepoDocument>(ReposCollection).FindOne(r => r.Name == name);

    public IReadOnlyList<RepoDocument> ListRepos() =>
        _db.GetCollection<RepoDocument>(ReposCollection).FindAll().OrderBy(r => r.Name).ToList();

    public uint GetNextRepoId() => NextId(ReposCollection);

    public IReadOnlyList<FileDocument> ListFiles(uint repoId) =>
        _db.GetCollection<FileDocument>(FilesCollection)
            .Find(f => f.RepoId == repoId)
            .OrderBy(f => f.Id)
            .ToList();

    public IReadOnlyList<FileDocument> ListAllFiles() =>
        _db.GetCollection<FileDocument>(FilesCollection).FindAll().OrderBy(f => f.Id).ToList();

    public FileDocument? FindFileById(uint id) => _db.GetCollection<FileDocument>(FilesCollection).FindById((long)id);

    public void UpsertFile(FileDocument file) => _db.GetCollection<FileDocument>(FilesCollection).Upsert(file);

    public void DeleteFile(uint id) => _db.GetCollection<FileDocument>(FilesCollection).Delete((long)id);

    public int CountFiles(uint repoId) => _db.GetCollection<FileDocument>(FilesCollection).Count(f => f.RepoId == repoId);

    public uint GetNextFileId() => NextId(FilesCollection);

    public IReadOnlyList<SymbolDocument> ListSymbolsByFile(uint fileId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).Find(s => s.FileId == fileId).ToList();

    public IReadOnlyList<SymbolDocument> ListSymbolsByRepo(uint repoId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).Find(s => s.RepoId == repoId).ToList();

    public void DeleteSymbolsByFile(uint fileId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).DeleteMany(s => s.FileId == fileId);

    public void InsertSymbols(IEnumerable<SymbolDocument> symbols) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).InsertBulk(symbols);

    public int CountSymbols(uint repoId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).Count(s => s.RepoId == repoId);

    public void DeleteRefsByFromSymbolIds(IReadOnlyCollection<long> symbolIds)
    {
        if (symbolIds.Count == 0)
        {
            return;
        }

        _db.GetCollection<RefDocument>(RefsCollection).DeleteMany(r => symbolIds.Contains(r.FromSymbolId));
    }

    public IReadOnlyList<RefDocument> ListRefsByFromSymbol(long symbolId) =>
        _db.GetCollection<RefDocument>(RefsCollection).Find(r => r.FromSymbolId == symbolId).ToList();

    public IReadOnlyList<RefDocument> ListRefsByFile(uint fileId) =>
        _db.GetCollection<RefDocument>(RefsCollection).Find(r => r.FileId == fileId).ToList();

    public void DeleteRefsByFile(uint fileId) =>
        _db.GetCollection<RefDocument>(RefsCollection).DeleteMany(r => r.FileId == fileId);

    public void DeleteRefsByFileIds(IReadOnlyCollection<uint> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        _db.GetCollection<RefDocument>(RefsCollection).DeleteMany(r => fileIds.Contains(r.FileId));
    }

    public void InsertRefs(IEnumerable<RefDocument> refs) =>
        _db.GetCollection<RefDocument>(RefsCollection).InsertBulk(refs);

    public IReadOnlyList<ChunkDocument> ListChunksByFile(uint fileId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).Find(c => c.FileId == fileId).ToList();

    public void DeleteChunksByFile(uint fileId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).DeleteMany(c => c.FileId == fileId);

    public void InsertChunks(IEnumerable<ChunkDocument> chunks) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).InsertBulk(chunks);

    public int CountChunks(uint repoId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).Count(c => c.RepoId == repoId);

    public int CountPendingChunks(uint? repoId, string? modelKey = null)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        if (modelKey is null)
        {
            return repoId.HasValue
                ? chunks.Count(c => c.RepoId == repoId.Value && c.EmbeddingStatus != "ok")
                : chunks.Count(c => c.EmbeddingStatus != "ok");
        }

        return repoId.HasValue
            ? chunks.Count(c => c.RepoId == repoId.Value && (c.EmbeddingStatus != "ok" || c.ModelKey != modelKey))
            : chunks.Count(c => c.EmbeddingStatus != "ok" || c.ModelKey != modelKey);
    }

    public IReadOnlyList<ChunkDocument> ListPendingChunks(uint repoId, string modelKey, int limit) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection)
            .Find(c => c.RepoId == repoId && (c.EmbeddingStatus != "ok" || c.ModelKey != modelKey))
            .OrderBy(c => c.Id)
            .Take(limit)
            .ToList();

    public void MarkChunksEmbedded(IEnumerable<long> chunkIds, string modelKey)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        foreach (var id in chunkIds)
        {
            var chunk = chunks.FindById(id);
            if (chunk is null)
            {
                continue;
            }

            chunk.EmbeddingStatus = "ok";
            chunk.ModelKey = modelKey;
            chunks.Update(chunk);
        }
    }

    public void MarkAllChunksPending(uint? repoId)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        var targets = repoId.HasValue
            ? chunks.Find(c => c.RepoId == repoId.Value)
            : chunks.FindAll();
        foreach (var chunk in targets)
        {
            chunk.EmbeddingStatus = "pending";
            chunk.ModelKey = null;
            chunks.Update(chunk);
        }
    }

    public IndexStateDocument? GetIndexState(string repoName) =>
        _db.GetCollection<IndexStateDocument>(IndexStateCollection).FindById(repoName);

    public void SaveIndexState(IndexStateDocument state) =>
        _db.GetCollection<IndexStateDocument>(IndexStateCollection).Upsert(state);

    public void Dispose() => _db.Dispose();

    private uint NextId(string name)
    {
        var last = _db.GetCollection(name).Find(Query.All(Query.Descending), 0, 1).FirstOrDefault();
        return last is null ? 0u : (uint)last["_id"].AsInt64 + 1u;
    }
}
