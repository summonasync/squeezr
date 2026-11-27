using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Cli.Embeddings;

public static class EmbeddingRuntimeFactory
{
    public static IReadOnlyList<string> TryCreate(
        string workspaceRoot,
        bool withProvider,
        out IEmbeddingProvider? provider,
        out IVectorStore? store)
        => TryCreate(workspaceRoot, withProvider, null, out provider, out store, out _);

    public static IReadOnlyList<string> TryCreate(
        string workspaceRoot,
        bool withProvider,
        out IEmbeddingProvider? provider,
        out IVectorStore? store,
        out EmbeddingProviderResolution? providerResolution)
        => TryCreate(workspaceRoot, withProvider, null, out provider, out store, out providerResolution);

    public static IReadOnlyList<string> TryCreate(
        string workspaceRoot,
        bool withProvider,
        HttpMessageHandler? httpHandler,
        out IEmbeddingProvider? provider,
        out IVectorStore? store,
        out EmbeddingProviderResolution? providerResolution)
    {
        provider = null;
        store = null;
        providerResolution = null;

        var configPath = SqueezrPaths.ConfigPath(workspaceRoot);
        if (!File.Exists(configPath))
        {
            return [$"config file not found at '{configPath}'; semantic and vector features are disabled."];
        }

        try
        {
            var config = SemanticConfigFile.Load(workspaceRoot);
            if (withProvider)
            {
                providerResolution = ResolveProvider(config.Embeddings, httpHandler);
                provider = EmbeddingProviderFactory.Create(
                    config.Embeddings with { Provider = providerResolution.Provider }, workspaceRoot, httpHandler);
            }

            store = new QdrantVectorStore(config.Qdrant, workspaceRoot);
            return [];
        }
        catch (WorkspaceException ex)
        {
            return providerResolution?.Reason is { } probeReason
                ? [$"ollama probe failed ({probeReason})", ex.Message]
                : [ex.Message];
        }
    }

    // The copy path needs one fact from the embeddings configuration and nothing else: the model key this
    // store would write. No provider and no vector store are constructed here — a copy that cannot name
    // its model has to refuse before a byte moves, not after a connection was opened for the question.
    public static string ResolveModelKey(string workspaceRoot, HttpMessageHandler? httpHandler = null)
    {
        var config = SemanticConfigFile.Load(workspaceRoot);
        var resolution = ResolveProvider(config.Embeddings, httpHandler);
        return EmbeddingModelKey.From(config.Embeddings with { Provider = resolution.Provider });
    }

    private static EmbeddingProviderResolution ResolveProvider(EmbeddingsConfig config, HttpMessageHandler? httpHandler)
    {
        try
        {
            using var http = httpHandler is null
                ? new HttpClient()
                : new HttpClient(httpHandler, disposeHandler: false);
            return new EmbeddingProviderResolver(http)
                .ResolveAsync(config.Provider, config.Ollama.Endpoint, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (WorkspaceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            throw new WorkspaceException($"Failed to resolve embeddings provider '{config.Provider}': {ex.Message}");
        }
    }
}
