using Squeezr.Core.Configuration;

namespace Squeezr.Tests;

public static class TestWorkspaceConfig
{
    public const string DeadQdrantEndpoint = "http://localhost:1";
    public const string DeadOllamaEndpoint = "http://localhost:1";

    public static void PointQdrantAtDeadEndpoint(string workspaceRoot)
    {
        var config = ConfigFile.Load(workspaceRoot);
        ConfigFile.Save(workspaceRoot, config with
        {
            Qdrant = config.Qdrant with { Endpoint = DeadQdrantEndpoint }
        });
    }

    public static void UseAutoEmbeddingsWithDeadOllama(string workspaceRoot)
    {
        var config = ConfigFile.Load(workspaceRoot);
        ConfigFile.Save(workspaceRoot, config with
        {
            Embeddings = config.Embeddings with
            {
                Provider = "auto",
                Ollama = config.Embeddings.Ollama with { Endpoint = DeadOllamaEndpoint }
            }
        });
    }

    public static void UseOnnxEmbeddings(string workspaceRoot)
    {
        var config = ConfigFile.Load(workspaceRoot);
        ConfigFile.Save(workspaceRoot, config with
        {
            Embeddings = config.Embeddings with { Provider = "onnx" }
        });
    }

    public static void SetRankExclude(string workspaceRoot, params string[] globs)
    {
        var config = ConfigFile.Load(workspaceRoot);
        ConfigFile.Save(workspaceRoot, config with
        {
            Indexing = config.Indexing with { RankExclude = globs }
        });
    }
}
