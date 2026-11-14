using Squeezr.Core.Storage;

namespace Squeezr.Core.Health;

public static class EmbeddingVintages
{
    // the count is asked of the whole workspace store rather than repo by repo: a chunk row that
    // carries a foreign key belongs to the vintage picture even once its repo row is gone.
    public static EmbeddingsHealth Measure(EmbeddingsHealth health, MetaStore store) =>
        health.ModelKey is { } modelKey
            ? health with { OtherModelChunks = store.CountChunksWithOtherModelKey(null, modelKey) }
            : health;
}
