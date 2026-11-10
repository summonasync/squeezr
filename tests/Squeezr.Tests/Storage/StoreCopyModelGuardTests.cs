using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// The guard is a meta.db read with no vector store behind it: every case here runs against a bare store
// directory with no config.json and no Qdrant anywhere, which is the point — a down vector endpoint must
// not be able to wave a known-incompatible copy through. The destination's model key arrives already
// resolved: it comes from the lane's own config and the caller settles it before any lock, so nothing here
// asks a provider anything at all.
public sealed class StoreCopyModelGuardTests
{
    private readonly string _root;

    public StoreCopyModelGuardTests()
    {
        _root = TestDirs.NewTempDir();
    }

    [Fact]
    public void Audit_SameModelKey_ReturnsTheNumbersTheCopyVerifiesAgainst()
    {
        var source = StoreFixture.Seed(Path.Combine(_root, "match", ".squeezr"));

        var audit = Audit(source.StoreDir, StoreFixture.ModelKey);

        audit.Files.ShouldBe(source.Files);
        audit.TotalChunks.ShouldBe(source.Chunks);
        audit.EmbeddedChunks.ShouldBe(source.Chunks);
        audit.ForeignChunks.ShouldBe(0);
    }

    [Fact]
    public void Audit_SourceCarriesAnotherModel_RefusesNamingCount_AndTheReindexRemedy()
    {
        var source = StoreFixture.Seed(Path.Combine(_root, "foreign", ".squeezr"), "ollama/qmdl/1024");

        var ex = Should.Throw<WorkspaceException>(() => Audit(source.StoreDir, StoreFixture.ModelKey));

        ex.Code.ShouldBe(WorkspaceException.WorktreeModelMismatchCode);
        ex.Message.ShouldContain($"{source.Chunks} embedded chunk(s)");
        // The key the destination resolves to is the fact the reader has to compare against.
        ex.Message.ShouldContain(StoreFixture.ModelKey);
        ex.Message.ShouldContain("--reindex");
        ex.Message.ShouldContain("no byte was copied");
    }

    // Never-embedded rows carry no vectors, so they cannot be inherited incompatibly.
    [Fact]
    public void Audit_ChunksStillPending_RefusesNothingAndCountsNoVectors()
    {
        var source = StoreFixture.Seed(Path.Combine(_root, "pending", ".squeezr"), modelKey: null);

        var audit = Audit(source.StoreDir, "ollama/whatever/768");

        audit.Files.ShouldBe(source.Files);
        audit.TotalChunks.ShouldBe(source.Chunks);
        audit.EmbeddedChunks.ShouldBe(0);
        audit.ForeignChunks.ShouldBe(0);
    }

    [Fact]
    public void Audit_MixedModelsInSource_CountsOnlyTheForeignRows()
    {
        var sourceDir = Path.Combine(_root, "mixed", ".squeezr");
        StoreFixture.Seed(sourceDir);
        using (var store = new MetaStore(MetaDb(sourceDir)))
        {
            store.InsertChunks([new ChunkDocument
            {
                Id = 900,
                FileId = 1,
                RepoId = 1,
                Text = "chunk from a foreign model",
                ChunkHash = "hash-foreign",
                EmbeddingStatus = "ok",
                ModelKey = "openai/text-embedding-3-small/1536",
            }]);
        }

        var ex = Should.Throw<WorkspaceException>(() => Audit(sourceDir, StoreFixture.ModelKey));

        // six rows carry the destination's model and one is foreign: the refusal counts only the foreign
        // rows, so a store on the right model is never re-queued as a reason to refuse the copy
        ex.Message.ShouldContain("1 embedded chunk(s)");
    }

    // The key arrives from the caller because a provider round trip must not happen inside the source lock.
    // A pass that comes without one while the source holds vectors is the race where the parent embedded
    // after the caller looked — it abstains with its own code, and the store it left empty gets filled by
    // the next run, which looks first.
    [Fact]
    public void Audit_SourceHasVectorsAndNoNamedModel_AbstainsWithTheUnresolvedCode()
    {
        var source = StoreFixture.Seed(Path.Combine(_root, "unresolved", ".squeezr"));

        var ex = Should.Throw<WorkspaceException>(() => Audit(source.StoreDir, null));

        ex.Code.ShouldBe(WorkspaceException.WorktreeModelUnresolvedCode);
        ex.Message.ShouldContain($"{source.Chunks} embedded chunk(s)");
        ex.Message.ShouldContain("re-run");
    }

    private SourceStoreAudit Audit(string storeDir, string? targetModelKey)
    {
        using var store = new MetaStore(MetaDb(storeDir));
        return StoreCopyModelGuard.Audit(store, targetModelKey);
    }

    private static string MetaDb(string storeDir) =>
        Path.Combine(storeDir, SqueezrPaths.MetaDbFileName);
}
