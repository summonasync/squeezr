using System.Net;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingRuntimeFactoryTests
{
    [Fact]
    public void TryCreate_MissingConfig_ReturnsNullsAndWarning()
    {
        var root = TestDirs.NewTempDir();

        var warnings = EmbeddingRuntimeFactory.TryCreate(root, withProvider: true, out var provider, out var store);

        provider.ShouldBeNull();
        store.ShouldBeNull();
        warnings.ShouldHaveSingleItem();
        warnings[0].ShouldContain("config file not found");
    }

    [Fact]
    public void TryCreate_ProviderFailure_DegradesWithWarningAndKeepsNulls()
    {
        var root = TestDirs.NewTempDir();
        SaveConfig(root, "SQUEEZR_TESTS_DEFINITELY_MISSING_KEY");

        var warnings = EmbeddingRuntimeFactory.TryCreate(root, withProvider: true, out var provider, out var store);

        provider.ShouldBeNull();
        store.ShouldBeNull();
        warnings.ShouldHaveSingleItem();
        warnings[0].ShouldContain("SQUEEZR_TESTS_DEFINITELY_MISSING_KEY");
    }

    [Fact]
    public void TryCreate_WithoutProvider_SkipsProviderAndBuildsStore()
    {
        var root = TestDirs.NewTempDir();
        SaveConfig(root, "SQUEEZR_TESTS_DEFINITELY_MISSING_KEY");

        var warnings = EmbeddingRuntimeFactory.TryCreate(root, withProvider: false, out var provider, out var store);

        provider.ShouldBeNull();
        store.ShouldNotBeNull();
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void TryCreate_InvalidQdrantEndpoint_ReturnsNullsAndWarning()
    {
        var root = TestDirs.NewTempDir();
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig { Provider = "openai" },
            Qdrant = new QdrantConfig { Endpoint = "not a uri" }
        });

        var warnings = EmbeddingRuntimeFactory.TryCreate(root, withProvider: false, out var provider, out var store);

        provider.ShouldBeNull();
        store.ShouldBeNull();
        warnings.ShouldHaveSingleItem();
        warnings[0].ShouldContain("not a uri");
    }

    [Fact]
    public void TryCreate_UnknownProvider_ReturnsNullsAndWarning()
    {
        var root = TestDirs.NewTempDir();
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig { Provider = "bert" }
        });

        var warnings = EmbeddingRuntimeFactory.TryCreate(root, withProvider: true, out var provider, out var store);

        provider.ShouldBeNull();
        store.ShouldBeNull();
        warnings.ShouldHaveSingleItem();
        warnings[0].ShouldContain("Unknown embeddings provider 'bert'");
    }

    [Fact]
    public void TryCreate_AutoWithReachableOllama_BuildsOllamaProviderFromResolvedProvider()
    {
        var root = TestDirs.NewTempDir();
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[{"name":"nomic-embed-text"}]}"""));
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig { Provider = "auto" }
        });

        var warnings = EmbeddingRuntimeFactory.TryCreate(
            root, withProvider: true, handler, out var provider, out _, out var resolution);

        warnings.ShouldBeEmpty();
        resolution.ShouldNotBeNull();
        resolution.Provider.ShouldBe("ollama");
        resolution.Reason.ShouldBeNull();
        provider.ShouldBeOfType<OllamaEmbeddingProvider>();
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public void TryCreate_AutoWithUnreachableOllama_FailsWithTheProbeHint()
    {
        var root = TestDirs.NewTempDir();
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig
            {
                Provider = "auto",
                Ollama = new OllamaOptions { Endpoint = TestWorkspaceConfig.DeadOllamaEndpoint }
            }
        });

        var warnings = EmbeddingRuntimeFactory.TryCreate(
            root, withProvider: true, out var provider, out var store, out var resolution);

        resolution.ShouldBeNull();
        provider.ShouldBeNull();
        store.ShouldBeNull();
        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("Ollama probe failed");
        warnings[0].ShouldContain("'ollama' or 'openai'");
    }

    [Fact]
    public void TryCreate_ExplicitOllama_ResolutionSkipsTheProbe()
    {
        var root = TestDirs.NewTempDir();
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[{"name":"nomic-embed-text"}]}"""));
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig { Provider = "ollama" }
        });

        var warnings = EmbeddingRuntimeFactory.TryCreate(
            root, withProvider: true, handler, out var provider, out _, out var resolution);

        warnings.ShouldBeEmpty();
        resolution.ShouldNotBeNull();
        resolution.Provider.ShouldBe("ollama");
        resolution.Reason.ShouldBeNull();
        provider.ShouldBeOfType<OllamaEmbeddingProvider>();
        handler.Requests.Count.ShouldBe(1);
    }

    private static void SaveConfig(string root, string apiKeyEnv)
    {
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Embeddings = new EmbeddingsConfig
            {
                Provider = "openai",
                OpenAiCompatible = new OpenAiCompatibleOptions { ApiKeyEnv = apiKeyEnv }
            }
        });
    }
}
