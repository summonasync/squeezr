using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class EmbeddingRuntimeFactory
{
    public static IReadOnlyList<string> TryCreate(
        string workspaceRoot,
        bool withProvider,
        out IEmbeddingProvider? provider,
        out IVectorStore? store)
    {
        provider = null;
        store = null;

        var configPath = SqueezrPaths.ConfigPath(workspaceRoot);
        if (!File.Exists(configPath))
        {
            return [$"config file not found at '{configPath}'; semantic and vector features are disabled."];
        }

        try
        {
            var config = ConfigFile.Load(workspaceRoot);
            if (withProvider)
            {
                provider = EmbeddingProviderFactory.Create(config.Embeddings, workspaceRoot);
            }

            store = new QdrantVectorStore(config.Qdrant, workspaceRoot);
            return [];
        }
        catch (WorkspaceException ex)
        {
            return [ex.Message];
        }
    }
}
