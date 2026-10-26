using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Configuration;

public class ConfigFileTests
{
    [Fact]
    public void Load_LegacyGitAwareKey_Ignored()
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

        config.Indexing.MaxFileSizeKb.ShouldBe(512);
        config.Indexing.NoSemantic.ShouldBeFalse();
    }

    [Fact]
    public void Load_ConfigWithoutEmbeddingsSection_UsesAutoDefaults()
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
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("auto");
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
                "maxFileSizeKb": 128
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
                "maxFileSizeKb": 512
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
                "maxFileSizeKb": 512
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
                "maxFileSizeKb": 512
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

    [Fact]
    public void Load_ConfigWithoutOllamaSection_UsesOllamaDefaults()
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
                "ignore": [],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Ollama.Endpoint.ShouldBe("http://127.0.0.1:11434");
        config.Embeddings.Ollama.Model.ShouldBe("nomic-embed-text");
        config.Embeddings.Ollama.Dimensions.ShouldBe(768);
        config.Embeddings.Ollama.ModelProfile.ShouldBe("nomic-embed-text-v1.5");
        config.Embeddings.Ollama.ResolvedProfile.ShouldNotBeNull();
        config.Embeddings.Ollama.ResolvedProfile!.Id.ShouldBe("nomic-embed-text-v1.5");
    }

    [Fact]
    public void Load_OllamaExplicitNullModelProfile_ResolvesToNoProfile()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "ollama",
                "ollama": {
                  "endpoint": "http://localhost:11434",
                  "model": "nomic-embed-text",
                  "dimensions": 768,
                  "modelProfile": null
                }
              },
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("ollama");
        config.Embeddings.Ollama.ModelProfile.ShouldBeNull();
        config.Embeddings.Ollama.ResolvedProfile.ShouldBeNull();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsOllamaSection()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig
            {
                Provider = "ollama",
                Ollama = new OllamaOptions
                {
                    Endpoint = "http://127.0.0.1:11434",
                    Model = "all-minilm",
                    Dimensions = 384,
                    ModelProfile = null
                }
            }
        };

        ConfigFile.Save(root, config);
        var loaded = ConfigFile.Load(root);

        loaded.Embeddings.Provider.ShouldBe("ollama");
        loaded.Embeddings.Ollama.Endpoint.ShouldBe("http://127.0.0.1:11434");
        loaded.Embeddings.Ollama.Model.ShouldBe("all-minilm");
        loaded.Embeddings.Ollama.Dimensions.ShouldBe(384);
        loaded.Embeddings.Ollama.ModelProfile.ShouldBeNull();
        loaded.Embeddings.Ollama.ResolvedProfile.ShouldBeNull();
    }

    [Fact]
    public void Load_UnknownOllamaModelProfile_ThrowsWorkspaceExceptionListingKnownIds()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "ollama",
                "ollama": { "modelProfile": "no-such-profile" }
              },
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var error = Should.Throw<WorkspaceException>(() => ConfigFile.Load(root));

        error.Message.ShouldContain(SqueezrPaths.ConfigPath(root));
        error.Message.ShouldContain("'no-such-profile'");
        error.Message.ShouldContain("nomic-embed-text-v1.5");
        error.Message.ShouldContain("bge-code-v1");
    }

    [Fact]
    public void Load_UnknownOpenAiModelProfile_ThrowsWorkspaceExceptionListingKnownIds()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": {
                "provider": "openai",
                "openAiCompatible": { "modelProfile": "no-such-profile" }
              },
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var error = Should.Throw<WorkspaceException>(() => ConfigFile.Load(root));

        error.Message.ShouldContain(SqueezrPaths.ConfigPath(root));
        error.Message.ShouldContain("'no-such-profile'");
    }

    [Fact]
    public void Load_RankExcludeAsBareString_SalvagesThePatternAndKeepsSiblings()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "embeddings": { "provider": "onnx" },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "rankExclude": "audits/**"
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Indexing.RankExclude.ShouldBe(["audits/**"]);
        config.Embeddings.Provider.ShouldBe("onnx");
        config.Chunking.MaxTokensPerChunk.ShouldBe(512);
        config.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public void Load_RankExcludeWithNonStringEntries_SkipsTheJunkKeepsTheStrings()
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
                "ignore": [],
                "maxFileSizeKb": 512,
                "rankExclude": [ "audits/**", 5, null, { "nested": true }, "BACKLOG/**" ]
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Indexing.RankExclude.ShouldBe(["audits/**", "BACKLOG/**"]);
        config.Embeddings.Provider.ShouldBe("auto");
    }

    [Fact]
    public void Load_RankExcludeAsScalar_ConfigStillLoadsAndTheListEmpties()
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
                "ignore": [],
                "maxFileSizeKb": 512,
                "rankExclude": 42
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Indexing.RankExclude.ShouldBeEmpty();
        config.Chunking.MaxTokensPerChunk.ShouldBe(512);
        config.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public void Save_ThenLoad_RankExcludeRoundTripsAsAProperArray()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                RankExclude = ["audits/**", "BACKLOG/**"]
            }
        };

        ConfigFile.Save(root, config);

        File.ReadAllText(SqueezrPaths.ConfigPath(root))
            .ShouldContain("\"rankExclude\": [\n");
        ConfigFile.Load(root).Indexing.RankExclude.ShouldBe(["audits/**", "BACKLOG/**"]);
    }

    [Fact]
    public void Load_NullEmbeddingsSection_UsesEmbeddingsDefaultsAndKeepsSiblings()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": null,
              "chunking": {
                "maxTokensPerChunk": 256,
                "fallbackWindowLines": 40,
                "fallbackOverlapLines": 10
              },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 },
              "qdrant": {
                "endpoint": "http://localhost:9999"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("auto");
        config.Embeddings.Onnx.ResolvedProfile.Id.ShouldBe("nomic-embed-text-v1.5");
        config.Embeddings.Ollama.Endpoint.ShouldBe("http://127.0.0.1:11434");
        config.Chunking.MaxTokensPerChunk.ShouldBe(256);
        config.Qdrant.Endpoint.ShouldBe("http://localhost:9999");
    }

    [Fact]
    public void Load_NullChunkingSection_UsesChunkingDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": { "provider": "onnx" },
              "chunking": null,
              "indexing": { "ignore": [], "maxFileSizeKb": 128 },
              "qdrant": { "endpoint": "http://localhost:6334" }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Chunking.MaxTokensPerChunk.ShouldBe(512);
        config.Chunking.FallbackWindowLines.ShouldBe(60);
        config.Chunking.FallbackOverlapLines.ShouldBe(15);
        config.Embeddings.Provider.ShouldBe("onnx");
        config.Indexing.MaxFileSizeKb.ShouldBe(128);
    }

    [Fact]
    public void Load_NullIndexingSection_UsesIndexingDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 256,
                "fallbackWindowLines": 40,
                "fallbackOverlapLines": 10
              },
              "indexing": null,
              "qdrant": { "endpoint": "http://localhost:6334" }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Indexing.Ignore.ShouldContain("**/node_modules/**");
        config.Indexing.MaxFileSizeKb.ShouldBe(512);
        config.Indexing.NoSemantic.ShouldBeFalse();
        config.Chunking.MaxTokensPerChunk.ShouldBe(256);
    }

    [Fact]
    public void Load_NullQdrantSection_UsesQdrantDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 256,
                "fallbackWindowLines": 40,
                "fallbackOverlapLines": 10
              },
              "indexing": { "ignore": [], "maxFileSizeKb": 128 },
              "qdrant": null
            }
            """);

        var config = ConfigFile.Load(root);

        config.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
        config.Indexing.MaxFileSizeKb.ShouldBe(128);
    }

    [Fact]
    public void Load_NullWatchSection_UsesWatchDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 256,
                "fallbackWindowLines": 40,
                "fallbackOverlapLines": 10
              },
              "indexing": { "ignore": [], "maxFileSizeKb": 128 },
              "qdrant": { "endpoint": "http://localhost:6334" },
              "watch": null
            }
            """);

        var config = ConfigFile.Load(root);

        config.Watch.DebounceSeconds.ShouldBe(2);
        config.Watch.BackoffMaxSeconds.ShouldBe(30);
        config.Watch.EmbedPending.ShouldBeTrue();
        config.Watch.EmbedBudgetPerPass.ShouldBe(WatchConfig.DefaultEmbedBudgetPerPass);
    }

    [Fact]
    public void Load_AllFiveSectionsNull_EverySectionDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": null,
              "chunking": null,
              "indexing": null,
              "qdrant": null,
              "watch": null
            }
            """);

        var config = ConfigFile.Load(root);

        var defaults = SqueezrConfig.CreateDefault();
        config.Embeddings.Provider.ShouldBe(defaults.Embeddings.Provider);
        config.Chunking.MaxTokensPerChunk.ShouldBe(defaults.Chunking.MaxTokensPerChunk);
        config.Indexing.Ignore.ShouldBe(defaults.Indexing.Ignore);
        config.Qdrant.Endpoint.ShouldBe(defaults.Qdrant.Endpoint);
        config.Watch.EmbedBudgetPerPass.ShouldBe(defaults.Watch.EmbedBudgetPerPass);
        _ = config.Embeddings.Onnx.ResolvedProfile;
        _ = config.Embeddings.Ollama.ResolvedProfile;
        _ = config.Embeddings.OpenAiCompatible.ResolvedProfile;
    }

    private static string NewWorkspaceWithConfig(string json)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
        return root;
    }
}
