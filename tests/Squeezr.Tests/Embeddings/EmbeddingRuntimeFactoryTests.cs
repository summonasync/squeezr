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
