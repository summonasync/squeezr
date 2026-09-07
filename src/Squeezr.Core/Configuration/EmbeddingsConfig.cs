namespace Squeezr.Core.Configuration;

public sealed record EmbeddingsConfig
{
    public string Provider { get; init; } = "onnx";

    public OnnxEmbeddingOptions Onnx { get; init; } = new();

    public OpenAiCompatibleOptions OpenAiCompatible { get; init; } = new();
}
