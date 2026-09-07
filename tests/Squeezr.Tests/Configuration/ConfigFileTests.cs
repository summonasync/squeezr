using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Configuration;

public class ConfigFileTests
{
    [Fact]
    public void Load_ConfigWithoutEmbeddingsSection_UsesOnnxDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [ "**/node_modules/**" ],
                "maxFileSizeKb": 512,
                "gitAware": true
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("onnx");
        config.Embeddings.Onnx.Dimensions.ShouldBe(1536);
        config.Embeddings.Onnx.MaxSequenceLength.ShouldBe(512);
        config.Embeddings.Onnx.BatchSize.ShouldBe(32);
        config.Embeddings.OpenAiCompatible.Dimensions.ShouldBe(1024);
        config.Chunking.MaxTokensPerChunk.ShouldBe(512);
        config.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public void Load_PartialEmbeddingsSection_FillsMissingOptionsWithDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "onnx"
              },
              "chunking": {
                "maxTokensPerChunk": 256,
                "fallbackWindowLines": 40,
                "fallbackOverlapLines": 10
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 128,
                "gitAware": false
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("onnx");
        config.Embeddings.Onnx.Dimensions.ShouldBe(1536);
        config.Embeddings.Onnx.ModelPath.ShouldBe("~/.squeezr/models/bge-code-v1.onnx");
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEmbeddingsSection()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig
            {
                Provider = "openai",
                OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = "http://localhost:11434/v1",
                    ApiKeyEnv = "CUSTOM_KEY",
                    Model = "nomic-embed-text",
                    Dimensions = 768
                }
            }
        };

        ConfigFile.Save(root, config);
        var loaded = ConfigFile.Load(root);

        loaded.Embeddings.Provider.ShouldBe("openai");
        loaded.Embeddings.OpenAiCompatible.Endpoint.ShouldBe("http://localhost:11434/v1");
        loaded.Embeddings.OpenAiCompatible.Model.ShouldBe("nomic-embed-text");
        loaded.Embeddings.OpenAiCompatible.Dimensions.ShouldBe(768);
        loaded.Embeddings.Onnx.Dimensions.ShouldBe(1536);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsWorkspaceException()
    {
        var root = NewWorkspaceWithConfig("{ not json");

        var error = Should.Throw<WorkspaceException>(() => ConfigFile.Load(root));

        error.Message.ShouldContain(SqueezrPaths.ConfigPath(root));
        error.Message.ShouldContain("not valid JSON");
    }

    private static string NewWorkspaceWithConfig(string json)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
        return root;
    }
}
