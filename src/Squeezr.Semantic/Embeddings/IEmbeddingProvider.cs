namespace Squeezr.Semantic.Embeddings;

public interface IEmbeddingProvider
{
    int Dimensions { get; }

    string ModelKey { get; }

    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage = EmbeddingUsage.Document, CancellationToken ct = default);
}
