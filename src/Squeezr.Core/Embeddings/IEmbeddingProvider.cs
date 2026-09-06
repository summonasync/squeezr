namespace Squeezr.Core.Embeddings;

public interface IEmbeddingProvider
{
    int Dimensions { get; }

    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
