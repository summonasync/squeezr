using System.Net;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingProviderFactoryTests
{
    [Fact]
    public void Create_UnknownProvider_ThrowsWorkspaceExceptionWithProviderName()
    {
        var config = new EmbeddingsConfig { Provider = "bert" };

        var error = Should.Throw<WorkspaceException>(() => EmbeddingProviderFactory.Create(config, TestDirs.NewTempDir()));

        error.Message.ShouldContain("'bert'");
        error.Message.ShouldContain("ollama, openai");
    }

    [Fact]
    public void Create_OllamaProvider_ReturnsProviderWithConfiguredDimensions()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest"}]}"""));
        var config = new EmbeddingsConfig
        {
            Provider = "ollama",
            Ollama = new OllamaOptions { Dimensions = 768 }
        };

        var provider = EmbeddingProviderFactory.Create(config, TestDirs.NewTempDir(), handler);

        var ollama = provider.ShouldBeOfType<OllamaEmbeddingProvider>();
        ollama.Dimensions.ShouldBe(768);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public void Create_OllamaProvider_ServerUnreachable_ThrowsWorkspaceExceptionWithServicesUpHint()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));
        var config = new EmbeddingsConfig { Provider = "ollama" };

        var error = Should.Throw<WorkspaceException>(() => EmbeddingProviderFactory.Create(config, TestDirs.NewTempDir(), handler));

        error.Message.ShouldContain("squeezr services up");
    }

    [Fact]
    public void Create_OpenAiProvider_MissingApiKeyEnv_ThrowsWorkspaceException()
    {
        var config = new EmbeddingsConfig
        {
            Provider = "openai",
            OpenAiCompatible = new OpenAiCompatibleOptions { ApiKeyEnv = "SQUEEZR_TESTS_DEFINITELY_MISSING_KEY" }
        };

        var error = Should.Throw<WorkspaceException>(() => EmbeddingProviderFactory.Create(config, TestDirs.NewTempDir()));

        error.Message.ShouldContain("SQUEEZR_TESTS_DEFINITELY_MISSING_KEY");
    }

    [Fact]
    public void Create_InvalidQdrantEndpoint_ThrowsWorkspaceException()
    {
        var error = Should.Throw<WorkspaceException>(() => new QdrantVectorStore(new QdrantConfig { Endpoint = "not a uri" }, TestDirs.NewTempDir()));

        error.Message.ShouldContain("not a uri");
    }
}
