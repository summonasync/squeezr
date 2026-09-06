namespace Squeezr.Core.Configuration;

public sealed record EmbeddingsConfig
{
    public required string Provider { get; init; }

    public required OnnxEmbeddingOptions Onnx { get; init; }

    public required OpenAiCompatibleOptions OpenAiCompatible { get; init; }
}
