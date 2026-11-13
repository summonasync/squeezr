using System.Net;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class OllamaEmbeddingProviderTests
{
    // what EmbeddingModelKey.From computes for Options(): no profile, so no profile suffix
    private const string ModelKey = "ollama/nomic-embed-text/768";

    private const string ProfiledModelKey = "ollama/nomic-embed-text/768/nomic-embed-text-v1.5";

    private static OllamaOptions Options(string endpoint = "http://localhost:11434") => new()
    {
        Endpoint = endpoint,
        Model = "nomic-embed-text",
        Dimensions = 768,
        ModelProfile = null
    };

    [Fact]
    public async Task EmbedAsync_PostsOllamaShapedRequest()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[1.0]]}"""));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
        var request = handler.Requests[0];
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe("/api/embed");
        request.Body.ShouldNotBeNull().ShouldContain("\"input\":[\"hello\"]");
        request.Body.ShouldNotBeNull().ShouldContain("\"model\":\"nomic-embed-text\"");
    }

    [Fact]
    public async Task EmbedAsync_ReturnsEmbeddingsInRequestOrder()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[1.0,2.0,3.0],[4.0,5.0,6.0]]}"""));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(["first text", "second text"], EmbeddingUsage.Document, CancellationToken.None);

        vectors.Length.ShouldBe(2);
        vectors[0].ShouldBe([1f, 2f, 3f]);
        vectors[1].ShouldBe([4f, 5f, 6f]);
    }

    [Fact]
    public async Task EmbedAsync_SplitsLargeInputIntoBatchesOf32()
    {
        var texts = Enumerable.Range(0, 33).Select(i => $"text-{i}").ToList();
        var firstBatch = "[" + string.Join(",", Enumerable.Repeat("[1.0]", 32)) + "]";
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, $"{{\"embeddings\":{firstBatch}}}"),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[2.0]]}"""));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(texts, EmbeddingUsage.Document, CancellationToken.None);

        handler.Requests.Count.ShouldBe(2);
        vectors.Length.ShouldBe(33);
        vectors[0].ShouldBe([1f]);
        vectors[32].ShouldBe([2f]);
    }

    [Fact]
    public async Task EmbedAsync_NomicProfile_SendsDocumentPrefixForDocuments()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[1.0]]}"""));
        var options = Options() with { ModelProfile = "nomic-embed-text-v1.5" };
        var provider = new OllamaEmbeddingProvider(options, ProfiledModelKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"search_document: hello\"]");
    }

    [Fact]
    public async Task EmbedAsync_NomicProfile_SendsQueryPrefixForQueries()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[1.0]]}"""));
        var options = Options() with { ModelProfile = "nomic-embed-text-v1.5" };
        var provider = new OllamaEmbeddingProvider(options, ProfiledModelKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"search_query: hello\"]");
    }

    [Fact]
    public async Task EmbedAsync_WithoutProfile_SendsNoPrefix()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[1.0]]}"""));
        var options = Options() with { ModelProfile = null };
        var provider = new OllamaEmbeddingProvider(options, ModelKey, handler);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain("\"input\":[\"hello\"]");
    }

    [Fact]
    public async Task EmbedAsync_RetriesOn429AndSucceeds()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json((HttpStatusCode)429, """{"error":"busy"}"""),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[7.0]]}"""));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        vectors[0].ShouldBe([7f]);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EmbedAsync_DoesNotRetryOn400()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"error":"bad request"}"""));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

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
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        await Should.ThrowAsync<HttpRequestException>(() => provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None));

        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EmbedAsync_RetriesOnNetworkError()
    {
        var handler = new StubHttpMessageHandler(
            new HttpRequestException("connection refused"),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"embeddings":[[7.0]]}"""));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        var vectors = await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        vectors[0].ShouldBe([7f]);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EmbedAsync_NetworkErrorExhausted_SuggestsServicesUpCommand()
    {
        var handler = new StubHttpMessageHandler(
            new HttpRequestException("connection refused"),
            new HttpRequestException("connection refused"),
            new HttpRequestException("connection refused"));
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, handler);

        var error = await Should.ThrowAsync<HttpRequestException>(() => provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None));

        error.Message.ShouldContain("squeezr services up");
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public void Dimensions_ComesFromConfig()
    {
        var provider = new OllamaEmbeddingProvider(Options(), ModelKey, new StubHttpMessageHandler());

        provider.Dimensions.ShouldBe(768);
        provider.ModelKey.ShouldBe(ModelKey);
    }
}
