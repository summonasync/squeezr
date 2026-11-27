using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Semantic.Embeddings;

public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingsConfig config, string workspaceRoot, HttpMessageHandler? httpHandler = null)
    {
        try
        {
            return CreateCore(config, workspaceRoot, httpHandler);
        }
        catch (WorkspaceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            throw new WorkspaceException($"Failed to initialize embeddings provider '{config.Provider}': {ex.Message}");
        }
    }

    private static IEmbeddingProvider CreateCore(EmbeddingsConfig config, string workspaceRoot, HttpMessageHandler? httpHandler)
    {
        switch (config.Provider.Trim().ToLowerInvariant())
        {
            case "openai":
                return new OpenAiCompatibleEmbeddingProvider(config.OpenAiCompatible, EmbeddingModelKey.From(config));
            case "ollama":
                OllamaModelEnsurer.Ensure(config.Ollama, httpHandler);
                return new OllamaEmbeddingProvider(config.Ollama, EmbeddingModelKey.From(config), httpHandler);
            default:
                throw new WorkspaceException($"Unknown embeddings provider '{config.Provider}'. Supported providers: ollama, openai.");
        }
    }
}
