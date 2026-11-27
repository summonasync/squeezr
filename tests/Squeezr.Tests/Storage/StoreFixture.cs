using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

// A store with known, small content: repo, file rows, embedded chunk rows carrying one model key and a
// trigram index with one live segment per file. The copy path reports counts, so every fixture here must
// have a count a test can name — a store built from nothing would let a copy that moved no bytes pass as
// a filled one.
internal static class StoreFixture
{
    internal sealed record Seeded(string StoreDir, int Files, int Chunks, int TrigramDocs);

    internal const string ModelKey = "ollama/nomic-embed-text/768";
    internal const string RepoName = "fixture";

    // A null model key seeds rows that were never embedded: they carry no vectors, so the copy guard has
    // nothing to compare and the fill costs no provider probe — the shape of a freshly minted parent.
    // The repo row is reused when the store already names it, because a store built by 'squeezr add' plus
    // this fixture must stay one row: two rows under one name is the state the copy's name join was built
    // to refuse.
    internal static Seeded Seed(
        string storeDir,
        string? modelKey = ModelKey,
        int files = 3,
        int chunksPerFile = 2,
        string repoName = RepoName,
        string repoPath = ".")
    {
        Directory.CreateDirectory(storeDir);
        var metaDbPath = Path.Combine(storeDir, SqueezrPaths.MetaDbFileName);
        var trigramPath = Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName);
        long nextChunkId = 0;

        using (var store = new MetaStore(metaDbPath, createIfMissing: true))
        {
            var repo = store.FindRepoIgnoringCase(repoName)
                ?? new RepoDocument { Id = store.GetNextRepoId(), Name = repoName, Path = repoPath };
            store.UpsertRepo(repo);

            var writer = new TrigramIndexWriter();
            for (uint file = 1; file <= files; file++)
            {
                store.UpsertFile(new FileDocument
                {
                    Id = file,
                    RepoId = repo.Id,
                    Path = $"src/File{file}.cs",
                    Sha256 = $"sha-{file}",
                    Language = "csharp",
                    Kind = "code",
                    SizeBytes = 128,
                    LineCount = 12,
                    IndexedAt = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
                });

                var chunks = Enumerable.Range(0, chunksPerFile)
                    .Select(index => new ChunkDocument
                    {
                        Id = ++nextChunkId,
                        FileId = file,
                        RepoId = repo.Id,
                        StartLine = index * 5 + 1,
                        EndLine = index * 5 + 5,
                        Text = $"chunk {file}-{index} of the seeded fixture store",
                        TokenCount = 12,
                        ChunkHash = $"hash-{file}-{index}",
                        VectorStatus = modelKey is null ? "pending" : "ok",
                        ModelKey = modelKey,
                    })
                    .ToList();
                store.InsertChunks(chunks);

                writer.AddDocument(new TrigramDocument(
                    repo.Id,
                    file,
                    TrigramExtractor.ExtractPostings($"fixture file {file} text")
                        .ToDictionary(hit => hit.Key, hit => hit.Value.ToArray())));
            }

            writer.Write(trigramPath);
        }

        return new Seeded(storeDir, files, files * chunksPerFile, files);
    }

    internal static (int Files, int Chunks) Counts(string storeDir)
    {
        using var store = new MetaStore(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName));
        var coverage = store.GetSemanticCoverage();
        return (store.CountAllFiles(), coverage.TotalChunks);
    }

    internal static int TrigramDocs(string storeDir) =>
        (int)new TrigramIndexReader(Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName)).DocCount;
}
