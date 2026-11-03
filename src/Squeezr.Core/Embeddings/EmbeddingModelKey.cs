using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Embeddings;

public static class EmbeddingModelKey
{
    public static string From(EmbeddingsConfig config)
    {
        var provider = config.Provider.Trim().ToLowerInvariant();
        if (provider == "auto")
            throw new WorkspaceException("Embeddings provider 'auto' must be resolved to a concrete provider before computing the model key.");
        if (provider == "openai")
        {
            var key = $"openai/{config.OpenAiCompatible.Model}/{config.OpenAiCompatible.Dimensions}";
            var profile = config.OpenAiCompatible.ResolvedProfile;
            return profile is null ? key : $"{key}/{profile.Id}";
        }

        if (provider == "ollama")
        {
            var key = $"ollama/{config.Ollama.Model}/{config.Ollama.Dimensions}";
            var profile = config.Ollama.ResolvedProfile;
            return profile is null ? key : $"{key}/{profile.Id}";
        }

        throw new WorkspaceException($"Embeddings provider '{config.Provider}' has no model key; supported providers: ollama, openai.");
    }
}
