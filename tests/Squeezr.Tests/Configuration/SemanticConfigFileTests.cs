using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Configuration;

// The composition seam the 130/T1 carve proved against ConfigFile: Core carries the semantic
// sections as opaque extension data, and this binder is the only place they become typed.
public class SemanticConfigFileTests
{
    [Fact]
    public void Load_FullProfileConfig_BindsTypedSemanticSections()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": { "provider": "ollama" },
              "chunking": { "maxTokensPerChunk": 256, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 128 },
              "qdrant": { "endpoint": "http://localhost:9999", "owned": true }
            }
            """);

        var config = SemanticConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("ollama");
        config.Embeddings.Ollama.Endpoint.ShouldBe("http://127.0.0.1:11434");
        config.Qdrant.Endpoint.ShouldBe("http://localhost:9999");
        config.Qdrant.Owned.ShouldBeTrue();
        config.Core.Indexing.MaxFileSizeKb.ShouldBe(128);
    }

    [Fact]
    public void Load_ConfigWithoutSemanticSections_UsesDefaults()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 }
            }
            """);

        var config = SemanticConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("auto");
        config.Qdrant.Endpoint.ShouldBe("");
        config.Qdrant.Owned.ShouldBeFalse();
    }

    [Fact]
    public void Load_NullSemanticSections_UsesDefaultsAndKeepsSiblings()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": null,
              "chunking": { "maxTokensPerChunk": 256, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 },
              "qdrant": null
            }
            """);

        var config = SemanticConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("auto");
        config.Qdrant.Endpoint.ShouldBe("");
        config.Core.Chunking.MaxTokensPerChunk.ShouldBe(256);
    }

    [Fact]
    public void Load_ProviderOnnx_ThrowsWithMigrationInstruction()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": { "provider": "onnx" },
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 }
            }
            """);

        var error = Should.Throw<WorkspaceException>(() => SemanticConfigFile.Load(root));

        error.Code.ShouldBe(WorkspaceException.OnnxProviderCode);
        error.Message.ShouldContain("Switch embeddings.provider to 'ollama' or 'openai'");
    }

    [Fact]
    public void Load_UnknownModelProfile_ThrowsNamedAfterTheConfigFile()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": { "provider": "ollama", "ollama": { "modelProfile": "not-a-profile" } },
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 }
            }
            """);

        var error = Should.Throw<WorkspaceException>(() => SemanticConfigFile.Load(root));

        error.Message.ShouldStartWith($"Config file at '{SqueezrPaths.ConfigPath(root)}':");
    }

    [Fact]
    public void Save_AfterAnEdit_RewritesTheSemanticSectionsIntoTheFile()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": { "provider": "ollama" },
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 },
              "qdrant": { "endpoint": "http://localhost:6334" }
            }
            """);

        var config = SemanticConfigFile.Load(root);
        SemanticConfigFile.Save(root, config with
        {
            Embeddings = config.Embeddings with { Provider = "openai" },
            Qdrant = config.Qdrant with { Owned = true }
        });

        var reloaded = SemanticConfigFile.Load(root);
        reloaded.Embeddings.Provider.ShouldBe("openai");
        reloaded.Qdrant.Owned.ShouldBeTrue();
        reloaded.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
        reloaded.Core.Chunking.MaxTokensPerChunk.ShouldBe(512);
        File.ReadAllText(SqueezrPaths.ConfigPath(root)).ShouldContain("\"embeddings\"");
    }

    [Fact]
    public void Save_PreservesSectionsCoreDoesNotModel()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "embeddings": { "provider": "ollama" },
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 },
              "custom": { "kept": true }
            }
            """);

        var config = SemanticConfigFile.Load(root);
        SemanticConfigFile.Save(root, config with { Indexing = config.Indexing with { MaxFileSizeKb = 256 } });

        File.ReadAllText(SqueezrPaths.ConfigPath(root)).ShouldContain("\"custom\"");
        ConfigFile.Load(root).Indexing.MaxFileSizeKb.ShouldBe(256);
    }

    [Fact]
    public void Bind_CoreWithNoExtensionData_BindsDefaultsTwice()
    {
        var first = SemanticConfig.Bind(SqueezrConfig.CreateDefault());
        var second = SemanticConfig.Bind(SqueezrConfig.CreateDefault());

        first.Embeddings.ShouldBe(second.Embeddings);
        first.Qdrant.ShouldBe(second.Qdrant);
    }

    private static string NewWorkspaceWithConfig(string json)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
        return root;
    }
}
