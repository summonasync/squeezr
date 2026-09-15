using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingModelKeyTests
{
    [Fact]
    public void From_DefaultOnnxConfig_IncludesResolvedProfileId()
    {
        var key = EmbeddingModelKey.From(new EmbeddingsConfig());

        key.ShouldBe("onnx/nomic-embed-text-v1.5.onnx/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void From_ExplicitBgeOnnxConfig_IncludesBgeProfileId()
    {
        var config = new EmbeddingsConfig
        {
            Onnx = new OnnxEmbeddingOptions
            {
                Model = "bge-code-v1",
                ModelPath = "~/.squeezr/models/bge-code-v1.onnx"
            }
        };

        var key = EmbeddingModelKey.From(config);

        key.ShouldBe("onnx/bge-code-v1.onnx/1536/bge-code-v1");
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
            Onnx = new OnnxEmbeddingOptions { Dimensions = 768 }
        };
        var bge = new EmbeddingsConfig
        {
            Onnx = new OnnxEmbeddingOptions { Model = "bge-code-v1", Dimensions = 768 }
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
    public void From_OllamaVsOnnxSameModel_DifferentProvidersProduceDifferentKeys()
    {
        var ollama = new EmbeddingsConfig { Provider = "ollama" };
        var onnx = new EmbeddingsConfig();

        EmbeddingModelKey.From(ollama).ShouldNotBe(EmbeddingModelKey.From(onnx));
    }
}
