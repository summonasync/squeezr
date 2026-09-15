namespace Squeezr.Cli.Embeddings;

internal interface IEmbeddingTextEncoder
{
    int FallbackTokenId { get; }

    IReadOnlyList<int> EncodeToIds(string text);
}
