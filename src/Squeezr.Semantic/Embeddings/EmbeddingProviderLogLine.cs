using Squeezr.Semantic.Embeddings;

namespace Squeezr.Semantic.Embeddings;

public static class EmbeddingProviderLogLine
{
    public static string? Describe(EmbeddingProviderResolution? resolution)
    {
        if (resolution is null)
        {
            return null;
        }

        return resolution.Reason is null
            ? $"embeddings provider: {resolution.Provider}"
            : $"embeddings provider: {resolution.Provider} (ollama unreachable: {resolution.Reason})";
    }
}
