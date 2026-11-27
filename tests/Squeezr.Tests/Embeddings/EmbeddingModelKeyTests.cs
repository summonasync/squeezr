using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingModelKeyTests
{
    [Fact]
    public void From_AutoProvider_ThrowsUnresolvedProvider()
    {
        var config = new EmbeddingsConfig { Provider = "auto" };

        var error = Should.Throw<WorkspaceException>(() => EmbeddingModelKey.From(config));

        error.Message.ShouldContain("auto");
    }

    [Fact]
    public void From_OpenAiWithoutProfile_KeepsLegacyKeyFormat()
    {
        var config = new EmbeddingsConfig
        {
            Provider = "openai",
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Model = "text-embedding-3-large",
                Dimensions = 1024
            }
        };

        var key = EmbeddingModelKey.From(config);

        key.ShouldBe("openai/text-embedding-3-large/1024");
    }

    [Fact]
    public void From_OpenAiWithProfile_AppendsProfileId()
    {
        var config = new EmbeddingsConfig
        {
            Provider = "openai",
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Model = "nomic-embed-text",
                Dimensions = 768,
                ModelProfile = "nomic-embed-text-v1.5"
            }
        };

        var key = EmbeddingModelKey.From(config);

        key.ShouldBe("openai/nomic-embed-text/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void From_SameDimensionsDifferentProfiles_ProduceDifferentKeys()
    {
        var nomic = new EmbeddingsConfig
        {
            Provider = "openai",
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Model = "nomic-embed-text",
                Dimensions = 768,
                ModelProfile = "nomic-embed-text-v1.5"
            }
        };
        var bge = new EmbeddingsConfig
        {
            Provider = "openai",
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Model = "nomic-embed-text",
                Dimensions = 768,
                ModelProfile = "bge-code-v1"
            }
        };

        EmbeddingModelKey.From(nomic).ShouldNotBe(EmbeddingModelKey.From(bge));
    }

    [Fact]
    public void From_OllamaConfig_IncludesProfileId()
    {
        var config = new EmbeddingsConfig { Provider = "ollama" };

        var key = EmbeddingModelKey.From(config);

        key.ShouldBe("ollama/nomic-embed-text/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void From_OllamaWithoutProfile_KeepsKeyWithoutProfileId()
    {
        var config = new EmbeddingsConfig
        {
            Provider = "ollama",
            Ollama = new OllamaOptions { ModelProfile = null }
        };

        var key = EmbeddingModelKey.From(config);

        key.ShouldBe("ollama/nomic-embed-text/768");
    }

    [Fact]
    public void From_UnknownProvider_Throws()
    {
        var config = new EmbeddingsConfig { Provider = "bert" };

        var error = Should.Throw<WorkspaceException>(() => EmbeddingModelKey.From(config));

        error.Message.ShouldContain("'bert'");
    }
}
