using Shouldly;
using Squeezr.Core.Health;
using Squeezr.Core.Storage;
using Squeezr.Semantic.Health;

namespace Squeezr.Tests.Health;

public sealed class EmbeddingVintagesTests
{
    private const string ActiveKey = TestEmbeddingKeys.Calibrated;
    private const string ForeignKey = "openai/text-embedding-3-large/1024";

    [Fact]
    public void Measure_TwoVintagesInTwoRepos_CountsEveryForeignRowOnce()
    {
        using var store = NewStore();
        store.AddRepo(new RepoDocument { Id = 1, Name = "alpha", Path = "alpha" });
        store.AddRepo(new RepoDocument { Id = 2, Name = "beta", Path = "beta" });
        store.InsertChunks([
            Chunk(1, "hash-a1", ActiveKey),
            Chunk(1, "hash-a2", ForeignKey),
            Chunk(2, "hash-b1", ForeignKey),
            Chunk(2, "hash-b2", ActiveKey),
            Chunk(2, "hash-b3", null, status: "pending"),
        ]);

        var health = EmbeddingVintages.Measure(HealthWithKey(ActiveKey), store);

        health.OtherModelChunks.ShouldBe(2);
        store.ListRepos().Sum(repo => store.CountChunksWithOtherModelKey(repo.Id, ActiveKey)).ShouldBe(2);
    }

    [Fact]
    public void Measure_SingleVintage_ReportsZeroAndNotSilence()
    {
        using var store = NewStore();
        store.AddRepo(new RepoDocument { Id = 1, Name = "alpha", Path = "alpha" });
        store.InsertChunks([Chunk(1, "hash-a1", ActiveKey)]);

        var health = EmbeddingVintages.Measure(HealthWithKey(ActiveKey), store);

        health.OtherModelChunks.ShouldBe(0);
    }

    [Fact]
    public void Measure_NothingEmbeddedYet_ReportsZeroNotABlank()
    {
        using var store = NewStore();
        store.AddRepo(new RepoDocument { Id = 1, Name = "alpha", Path = "alpha" });
        store.InsertChunks([Chunk(1, "hash-a1", null, status: "pending")]);

        var health = EmbeddingVintages.Measure(HealthWithKey(ActiveKey), store);

        health.OtherModelChunks.ShouldBe(0);
    }

    [Fact]
    public void Measure_HealthWithoutKey_StaysSilentAndOpensNoCount()
    {
        using var store = NewStore();
        store.AddRepo(new RepoDocument { Id = 1, Name = "alpha", Path = "alpha" });
        store.InsertChunks([Chunk(1, "hash-a1", ForeignKey)]);

        var health = EmbeddingVintages.Measure(EmbeddingsHealth.Silent, store);

        health.ModelKey.ShouldBeNull();
        health.OtherModelChunks.ShouldBeNull();
    }

    [Fact]
    public void Measure_KeepsEveryOtherFieldOfTheHealthItWasGiven()
    {
        using var store = NewStore();
        store.AddRepo(new RepoDocument { Id = 1, Name = "alpha", Path = "alpha" });
        store.InsertChunks([Chunk(1, "hash-a1", ForeignKey), Chunk(1, "hash-a2", ActiveKey)]);
        var probe = new EmbeddingsHealth("ollama", null, "embedding model runs on CPU — embeddings will be slow", ActiveKey);

        var health = EmbeddingVintages.Measure(probe, store);

        health.Provider.ShouldBe("ollama");
        health.CpuOnly.ShouldBe(probe.CpuOnly);
        health.ModelKey.ShouldBe(ActiveKey);
        health.OtherModelChunks.ShouldBe(1);
    }

    private static MetaStore NewStore() => new(Path.Combine(TestDirs.NewTempDir(), "meta.db"), createIfMissing: true);

    private static EmbeddingsHealth HealthWithKey(string modelKey) =>
        new(null, null, null, modelKey);

    private static ChunkDocument Chunk(uint repoId, string hash, string? modelKey, string status = "ok") => new()
    {
        FileId = repoId,
        RepoId = repoId,
        StartLine = 1,
        EndLine = 4,
        Text = $"body {hash}",
        ChunkHash = hash,
        VectorStatus = status,
        ModelKey = modelKey,
    };
}
