namespace Squeezr.Core.Configuration;

public sealed record OnnxEmbeddingOptions
{
    public required string ModelPath { get; init; }

    public required string TokenizerPath { get; init; }

    public required int Dimensions { get; init; }

    public required int MaxSequenceLength { get; init; }

    public required int BatchSize { get; init; }
}
