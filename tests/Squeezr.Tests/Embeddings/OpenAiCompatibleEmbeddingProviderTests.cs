using System.Net;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Embeddings;

[Collection("ProcessGlobals")]
public class OpenAiCompatibleEmbeddingProviderTests : IDisposable
{
    private const string ApiKeyEnv = "SQUEEZR_TESTS_EMBEDDINGS_KEY";

    // what EmbeddingModelKey.From computes for Options(): no profile, so no profile suffix
    private const string ModelKey = "openai/nomic-embed-text/768";

    private const string NomicProfiledKey = "openai/nomic-embed-text/768/nomic-embed-text-v1.5";

    private const string BgeProfiledKey = "openai/nomic-embed-text/768/bge-code-v1";

    public OpenAiCompatibleEmbeddingProviderTests()
        => Environment.SetEnvironmentVariable(ApiKeyEnv, "test-key");

    public void Dispose()
        => Environment.SetEnvironmentVariable(ApiKeyEnv, null);

    private static OpenAiCompatibleOptions Options(string endpoint = "http://localhost:11434/v1") => new()
    {
        Endpoint = endpoint,
        ApiKeyEnv = ApiKeyEnv,
        Model = "nomic-embed-text",
        Dimensions = 768
    };

    [Fact]
    public async Task EmbedAsync_ReturnsVectorsPlacedByIndex()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"data":[{"index":1,"embedding":[4.0,5.0,6.0]},{"index":0,"embedding":[1.0,2.0,3.0]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(["first text", "second text"], EmbeddingUsage.Document, CancellationToken.None);

        vectors.Length.ShouldBe(2);
        vectors[0].ShouldBe([1f, 2f, 3f]);
        vectors[1].ShouldBe([4f, 5f, 6f]);
    }

    [Fact]
    public async Task EmbedAsync_PostsOpenAiShapedRequest()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"data":[{"index":0,"embedding":[1.0]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
        var request = handler.Requests[0];
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe("/v1/embeddings");
        request.Body.ShouldNotBeNull().ShouldContain("\"input\":[\"hello\"]");
        request.Body.ShouldNotBeNull().ShouldContain("\"model\":\"nomic-embed-text\"");
    }

    [Fact]
    public async Task EmbedAsync_RetriesOn429AndSucceeds()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json((HttpStatusCode)429, """{"error":"rate limited"}"""),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        vectors[0].ShouldBe([1f]);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EmbedAsync_DoesNotRetryOn400()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"error":"bad request"}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        var error = await Should.ThrowAsync<HttpRequestException>(() => provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None));

        error.Message.ShouldContain("400");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EmbedAsync_GivesUpAfterThreeAttemptsOn500()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"),
            StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"),
            StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        await Should.ThrowAsync<HttpRequestException>(() => provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None));

        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EmbedAsync_RetriesOnNetworkError()
    {
        var handler = new StubHttpMessageHandler(
            new HttpRequestException("connection refused"),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[7.0]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        vectors[0].ShouldBe([7f]);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EmbedAsync_NomicProfile_SendsDocumentPrefixForDocuments()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0]}]}"""));
        var options = Options() with { ModelProfile = "nomic-embed-text-v1.5" };
        var provider = new OpenAiCompatibleEmbeddingProvider(options, NomicProfiledKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"search_document: hello\"]");
    }

    [Fact]
    public async Task EmbedAsync_NomicProfile_SendsQueryPrefixForQueries()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0]}]}"""));
        var options = Options() with { ModelProfile = "nomic-embed-text-v1.5" };
        var provider = new OpenAiCompatibleEmbeddingProvider(options, NomicProfiledKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"search_query: hello\"]");
    }

    [Fact]
    public async Task EmbedAsync_WithoutProfile_SendsNoPrefixForQueries()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"hello\"]");
    }

    [Fact]
    public async Task EmbedAsync_BgeProfile_SendsRawTextWithoutPrefix()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0]}]}"""));
        var options = Options() with { ModelProfile = "bge-code-v1" };
        var provider = new OpenAiCompatibleEmbeddingProvider(options, BgeProfiledKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"hello\"]");
    }

    [Fact]
    public void Constructor_ThrowsWhenApiKeyEnvIsMissing()
    {
        Environment.SetEnvironmentVariable(ApiKeyEnv, null);

        var error = Should.Throw<InvalidOperationException>(
            () => new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey));

        error.Message.ShouldContain(ApiKeyEnv);
    }

    [Fact]
    public void Dimensions_ComesFromConfig()
    {
        var provider = new OpenAiCompatibleEmbeddingProvider(Options(), ModelKey, new StubHttpMessageHandler());
        provider.Dimensions.ShouldBe(768);
        provider.ModelKey.ShouldBe(ModelKey);
    }

    [Fact]
    public void Factory_OpenAiProvider_CarriesTheKeyFromComputesForThatConfig()
    {
        var config = new EmbeddingsConfig
        {
            Provider = "openai",
            OpenAiCompatible = Options() with { Model = "squeezr-unknown-embed", Dimensions = 512 }
        };

        var provider = EmbeddingProviderFactory.Create(config, TestDirs.NewTempDir());

        provider.ModelKey.ShouldBe(EmbeddingModelKey.From(config));
        provider.ModelKey.ShouldBe("openai/squeezr-unknown-embed/512");
        WeakMatchPolicy.HasCalibratedFloor(provider.ModelKey).ShouldBeFalse();
    }
}
