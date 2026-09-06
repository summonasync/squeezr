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
        }
        _db.GetCollection(ReposCollection).EnsureIndex("name", unique: true);
        _db.GetCollection(FilesCollection).EnsureIndex("repoId");
        _db.GetCollection(SymbolsCollection).EnsureIndex("fileId");
        _db.GetCollection(RefsCollection).EnsureIndex("fromSymbolId");
        _db.GetCollection(ChunksCollection).EnsureIndex("fileId");
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
