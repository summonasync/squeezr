using Squeezr.Core.Configuration;

namespace Squeezr.Core.Embeddings;

public static class EmbeddingModelKey
{
    public static string From(EmbeddingsConfig config)
    {
        var provider = config.Provider.Trim().ToLowerInvariant();
        return provider == "openai"
            ? $"openai/{config.OpenAiCompatible.Model}/{config.OpenAiCompatible.Dimensions}"
            : $"onnx/{Path.GetFileName(config.Onnx.ModelPath)}/{config.Onnx.Dimensions}";
    }
}
