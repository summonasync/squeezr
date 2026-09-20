using System.Net;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingProviderResolverTests
{
    [Fact]
    public async Task ResolveAsync_AutoWithReachableOllama_ReturnsOllama()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var resolver = new EmbeddingProviderResolver(new HttpClient(handler));

        var resolution = await resolver.ResolveAsync("auto", "http://localhost:11434", CancellationToken.None);

        resolution.Provider.ShouldBe("ollama");
        resolution.Reason.ShouldBeNull();
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe("GET");
        handler.Requests[0].Path.ShouldBe("/api/tags");
    }

    [Fact]
    public async Task ResolveAsync_AutoWithNonSuccessStatus_ReturnsOnnxWithStatusReason()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var resolver = new EmbeddingProviderResolver(new HttpClient(handler));

        var resolution = await resolver.ResolveAsync("auto", "http://localhost:11434", CancellationToken.None);

        resolution.Provider.ShouldBe("onnx");
        resolution.Reason.ShouldNotBeNull().ShouldContain("503");
    }

    [Fact]
    public async Task ResolveAsync_AutoWithDeadEndpoint_ReturnsOnnxWithReason()
    {
        var resolver = new EmbeddingProviderResolver(new HttpClient());

        var resolution = await resolver.ResolveAsync("auto", "http://localhost:1", CancellationToken.None);

        resolution.Provider.ShouldBe("onnx");
        resolution.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ResolveAsync_AutoWhenProbeTimesOut_ReturnsOnnxWithTimeoutReason()
    {
        var resolver = new EmbeddingProviderResolver(
            new HttpClient(new StalledHttpMessageHandler()), TimeSpan.FromMilliseconds(200));

        var resolution = await resolver.ResolveAsync("auto", "http://localhost:11434", CancellationToken.None);

        resolution.Provider.ShouldBe("onnx");
        resolution.Reason.ShouldNotBeNull().ShouldContain("timed out");
    }

    [Theory]
    [InlineData("onnx", "onnx")]
    [InlineData("ollama", "ollama")]
    [InlineData("openai", "openai")]
    [InlineData(" Ollama ", "ollama")]
    public async Task ResolveAsync_ExplicitProvider_PassesThroughWithoutProbing(string configured, string expected)
    {
        var handler = new StubHttpMessageHandler();
        var resolver = new EmbeddingProviderResolver(new HttpClient(handler));

        var resolution = await resolver.ResolveAsync(configured, "http://localhost:1", CancellationToken.None);

        resolution.Provider.ShouldBe(expected);
        resolution.Reason.ShouldBeNull();
        handler.Requests.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_UnknownProvider_ThrowsWorkspaceException()
    {
        var resolver = new EmbeddingProviderResolver(new HttpClient(new StubHttpMessageHandler()));

        var error = await Should.ThrowAsync<WorkspaceException>(
            () => resolver.ResolveAsync("bert", "http://localhost:11434", CancellationToken.None));

        error.Message.ShouldContain("'bert'");
    }

    private sealed class StalledHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new HttpRequestException("probe stalled past the client timeout");
        }
    }
}
