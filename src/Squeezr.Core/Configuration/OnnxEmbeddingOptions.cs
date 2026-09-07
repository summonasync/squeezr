namespace Squeezr.Core.Configuration;

public sealed record OnnxEmbeddingOptions
{
    public string ModelPath { get; init; } = "~/.squeezr/models/bge-code-v1.onnx";

    public string TokenizerPath { get; init; } = "~/.squeezr/models/bge-code-v1/tokenizer.json";

    public int Dimensions { get; init; } = 1536;

    public int MaxSequenceLength { get; init; } = 512;

    public int BatchSize { get; init; } = 32;
}
