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
        config.Embeddings.Onnx.Model.ShouldBeNull();
        config.Embeddings.Onnx.Dimensions.ShouldBeNull();
        config.Embeddings.Onnx.MaxSequenceLength.ShouldBeNull();
        config.Embeddings.Onnx.ResolvedProfile.Id.ShouldBe("nomic-embed-text-v1.5");
        config.Embeddings.Onnx.EffectiveDimensions.ShouldBe(768);
        config.Embeddings.Onnx.EffectiveMaxSequenceLength.ShouldBe(512);
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
        config.Embeddings.Onnx.Dimensions.ShouldBeNull();
        config.Embeddings.Onnx.ResolvedProfile.Id.ShouldBe("nomic-embed-text-v1.5");
        config.Embeddings.Onnx.ModelPath.ShouldBe("~/.squeezr/models/nomic-embed-text-v1.5.onnx");
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
        loaded.Embeddings.Onnx.Dimensions.ShouldBeNull();
        loaded.Embeddings.Onnx.ResolvedProfile.Id.ShouldBe("nomic-embed-text-v1.5");
    }

    [Fact]
    public void Load_MalformedJson_ThrowsWorkspaceException()
    {
        var root = NewWorkspaceWithConfig("{ not json");

        var error = Should.Throw<WorkspaceException>(() => ConfigFile.Load(root));

        error.Message.ShouldContain(SqueezrPaths.ConfigPath(root));
        error.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void Load_OnnxModelBgeCodeV1_ResolvesBgeProfile()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "onnx",
                "onnx": { "model": "bge-code-v1" }
              },
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "gitAware": true
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Onnx.Model.ShouldBe("bge-code-v1");
        config.Embeddings.Onnx.Dimensions.ShouldBeNull();
        config.Embeddings.Onnx.ResolvedProfile.Id.ShouldBe("bge-code-v1");
        config.Embeddings.Onnx.EffectiveDimensions.ShouldBe(1536);
    }

    [Fact]
    public void Load_OnnxExplicitDimensions_TakePriorityOverProfile()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "onnx",
                "onnx": {
                  "model": "bge-code-v1",
                  "dimensions": 256,
                  "maxSequenceLength": 128
                }
              },
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "gitAware": true
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Onnx.Dimensions.ShouldBe(256);
        config.Embeddings.Onnx.MaxSequenceLength.ShouldBe(128);
        config.Embeddings.Onnx.EffectiveDimensions.ShouldBe(256);
        config.Embeddings.Onnx.EffectiveMaxSequenceLength.ShouldBe(128);
    }

    [Fact]
    public void Load_UnknownOnnxModel_ThrowsWorkspaceExceptionListingKnownIds()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "onnx",
                "onnx": { "model": "no-such-model" }
              },
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "gitAware": true
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var error = Should.Throw<WorkspaceException>(() => ConfigFile.Load(root));

        error.Message.ShouldContain(SqueezrPaths.ConfigPath(root));
        error.Message.ShouldContain("'no-such-model'");
        error.Message.ShouldContain("nomic-embed-text-v1.5");
        error.Message.ShouldContain("bge-code-v1");
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsOnnxModelAndExplicitOverrides()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig
            {
                Onnx = new OnnxEmbeddingOptions
                {
                    Model = "bge-code-v1",
                    Dimensions = 256,
                    MaxSequenceLength = 128
                }
            }
        };

        ConfigFile.Save(root, config);
        var loaded = ConfigFile.Load(root);

        loaded.Embeddings.Onnx.Model.ShouldBe("bge-code-v1");
        loaded.Embeddings.Onnx.Dimensions.ShouldBe(256);
        loaded.Embeddings.Onnx.MaxSequenceLength.ShouldBe(128);
        loaded.Embeddings.Onnx.EffectiveDimensions.ShouldBe(256);
        loaded.Embeddings.Onnx.ResolvedProfile.Id.ShouldBe("bge-code-v1");
    }

    private static string NewWorkspaceWithConfig(string json)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
        return root;
    }
}
