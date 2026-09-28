using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class MetaStoreChunkStatusTests
{
    [Fact]
    public void ListPendingChunks_ReturnsPending_AndExcludesEmbedded()
    {
        using var store = NewStore();
        var first = Chunk(1, 1, "pending", null);
        var second = Chunk(1, 2, "pending", null);
        store.InsertChunks([Chunk(1, 1, "ok", "m1"), first, second]);

        var pending = store.ListPendingChunks(1, "m1", 100);

        pending.Select(c => c.Id).ShouldBe([first.Id, second.Id]);
    }

    [Fact]
    public void ListPendingChunks_ModelKeyMismatch_IsPendingAgain()
    {
        using var store = NewStore();
        store.InsertChunks([Chunk(1, 1, "ok", "old-model")]);

        store.ListPendingChunks(1, "new-model", 100).ShouldHaveSingleItem();
        store.ListPendingChunks(1, "old-model", 100).ShouldBeEmpty();
    }

    [Fact]
    public void ListPendingChunks_RespectsLimit()
    {
        using var store = NewStore();
        store.InsertChunks(Enumerable.Range(0, 5).Select(i => Chunk(1, (uint)i, "pending", null)));

        store.ListPendingChunks(1, "m1", 2).Count.ShouldBe(2);
    }

    [Fact]
    public void ListPendingChunks_FiltersByRepo()
    {
        using var store = NewStore();
        store.InsertChunks([Chunk(1, 1, "pending", null), Chunk(2, 1, "pending", null)]);

        store.ListPendingChunks(1, "m1", 100).ShouldHaveSingleItem().RepoId.ShouldBe(1u);
    }

    [Fact]
    public void MarkChunksEmbedded_SetsStatusAndModelKey()
    {
        using var store = NewStore();
        var chunks = new[] { Chunk(1, 1, "pending", null), Chunk(1, 2, "pending", null) };
        store.InsertChunks(chunks);

        store.MarkChunksEmbedded(chunks.Select(c => c.Id), "m1");

        store.ListPendingChunks(1, "m1", 100).ShouldBeEmpty();
        store.ListChunksByFile(1).ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == "m1");
    }

    [Fact]
    public void MarkChunksEmbedded_UnknownIds_AreIgnored()
    {
        using var store = NewStore();
        var chunk = Chunk(1, 1, "pending", null);
        store.InsertChunks([chunk]);

        store.MarkChunksEmbedded([999L, chunk.Id], "m1");

        store.ListPendingChunks(1, "m1", 100).ShouldBeEmpty();
    }

    [Fact]
    public void MarkAllChunksPending_WithoutRepoId_ResetsEverything()
    {
        using var store = NewStore();
        store.InsertChunks(
        [
            Chunk(1, 1, "ok", "m1"),
            Chunk(2, 1, "ok", "m1"),
            Chunk(1, 2, "pending", null)
        ]);

        store.MarkAllChunksPending(null);

        store.ListPendingChunks(1, "m1", 100).Count.ShouldBe(2);
        store.ListPendingChunks(2, "m1", 100).ShouldHaveSingleItem();
        store.ListChunksByFile(1).ShouldAllBe(c => c.EmbeddingStatus == "pending" && c.ModelKey == null);
    }

    [Fact]
    public void MarkAllChunksPending_WithRepoId_ResetsOnlyThatRepo()
    {
        using var store = NewStore();
        store.InsertChunks([Chunk(1, 1, "ok", "m1"), Chunk(2, 1, "ok", "m1")]);

        store.MarkAllChunksPending(1);

        store.ListPendingChunks(1, "m1", 100).ShouldHaveSingleItem();
        store.ListPendingChunks(2, "m1", 100).ShouldBeEmpty();
    }

    [Fact]
    public void CountPendingChunks_CountsNonOk_AcrossRepos()
    {
        using var store = NewStore();
        store.InsertChunks(
        [
            Chunk(1, 1, "pending", null),
            Chunk(1, 2, "ok", "m1"),
            Chunk(2, 1, "pending", null),
            Chunk(2, 2, "ok", "m1")
        ]);

        store.CountPendingChunks(null).ShouldBe(2);
        store.CountPendingChunks(1).ShouldBe(1);
        store.CountPendingChunks(2).ShouldBe(1);
        store.CountPendingChunks(99).ShouldBe(0);
    }

    [Fact]
    public void GetSemanticCoverage_EmptyStore_HasNullRatio()
    {
        using var store = NewStore();

        var coverage = store.GetSemanticCoverage();

        coverage.ShouldBe(new SemanticCoverage(0, 0));
        coverage.Ratio.ShouldBeNull();
    }

    [Fact]
    public void GetSemanticCoverage_MixedStatuses_SpansAllRepos()
    {
        using var store = NewStore();
        store.InsertChunks(
        [
            Chunk(1, 1, "pending", null),
            Chunk(1, 2, "ok", "m1"),
            Chunk(2, 1, "pending", null),
            Chunk(2, 2, "ok", "m1")
        ]);

        var coverage = store.GetSemanticCoverage();

        coverage.ShouldBe(new SemanticCoverage(4, 2));
        coverage.Ratio.ShouldBe(0.5);
    }

    [Fact]
    public void GetSemanticCoverage_AllEmbedded_RatioIsOne()
    {
        using var store = NewStore();
        store.InsertChunks([Chunk(1, 1, "ok", "m1"), Chunk(1, 2, "ok", "m1")]);

        var coverage = store.GetSemanticCoverage();

        coverage.ShouldBe(new SemanticCoverage(2, 0));
        coverage.Ratio.ShouldBe(1.0);
    }

    private static MetaStore NewStore() =>
        new(Path.Combine(TestDirs.NewTempDir(), "meta.db"), createIfMissing: true);

    private static ChunkDocument Chunk(uint repoId, uint fileId, string status, string? modelKey) =>
        new()
        {
            RepoId = repoId,
            FileId = fileId,
            Text = "snippet",
            ChunkHash = $"{repoId}-{fileId}",
            EmbeddingStatus = status,
            ModelKey = modelKey
        };
}
