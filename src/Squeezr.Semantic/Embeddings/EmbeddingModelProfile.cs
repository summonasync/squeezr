namespace Squeezr.Semantic.Embeddings;

public sealed record EmbeddingModelProfile
{
    public required string Id { get; init; }

    public required int Dimensions { get; init; }

    public required EmbeddingPooling Pooling { get; init; }

    public string? DocumentPrefix { get; init; }

    public string? QueryPrefix { get; init; }

    public required int DefaultMaxSequenceLength { get; init; }

    public string? PrefixFor(EmbeddingUsage usage) => usage == EmbeddingUsage.Query ? QueryPrefix : DocumentPrefix;
}
