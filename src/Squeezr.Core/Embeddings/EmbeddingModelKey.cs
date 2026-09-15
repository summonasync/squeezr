using Squeezr.Core.Configuration;

namespace Squeezr.Core.Embeddings;

public static class EmbeddingModelKey
{
    public static string From(EmbeddingsConfig config)
    {
        var provider = config.Provider.Trim().ToLowerInvariant();
        if (provider == "openai")
        {
            var key = $"openai/{config.OpenAiCompatible.Model}/{config.OpenAiCompatible.Dimensions}";
            var profile = config.OpenAiCompatible.ResolvedProfile;
            return profile is null ? key : $"{key}/{profile.Id}";
        }

        var onnx = config.Onnx;
        return $"onnx/{Path.GetFileName(onnx.ModelPath)}/{onnx.EffectiveDimensions}/{onnx.ResolvedProfile.Id}";
    }
}
