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

    [Fact]
    public async Task ResolveAsync_AutoWithStalledIpv6Loopback_PicksIpv4OllamaDeterministically()
    {
        var handler = new DualAddressStubHandler();
        var resolver = new EmbeddingProviderResolver(new HttpClient(handler), TimeSpan.FromSeconds(5));

        var resolution = await resolver.ResolveAsync("auto", "http://localhost:11434", CancellationToken.None);

        resolution.Provider.ShouldBe("ollama");
        resolution.Reason.ShouldBeNull();
        handler.Hosts.ShouldHaveSingleItem().ShouldBe("127.0.0.1");
    }

    [Fact]
    public void DefaultProbeTimeout_IsAtLeastWindowsSynRefusalWindow()
    {
        EmbeddingProviderResolver.DefaultProbeTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3));
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

    private sealed class DualAddressStubHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new HttpRequestException("ipv6-shaped connect was refused");
            }

            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        }
    }
}
