using System.Net;
using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Embeddings;

public class OllamaStatusProbeTests
{
    private static OllamaOptions Options => new()
    {
        Endpoint = "http://localhost:11434",
        Model = "nomic-embed-text",
    };

    [Fact]
    public async Task Probe_TagsContainModel_ReturnsAvailableWithModelInstalled()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest"}]}"""));

        var status = await OllamaStatusProbe.ProbeAsync(Options, CancellationToken.None, handler);

        status.Available.ShouldBeTrue();
        status.ModelInstalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Probe_TagsMissingModel_ReturnsAvailableWithoutModel()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""));

        var status = await OllamaStatusProbe.ProbeAsync(Options, CancellationToken.None, handler);

        status.Available.ShouldBeTrue();
        status.ModelInstalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_ServerUnreachable_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));

        var status = await OllamaStatusProbe.ProbeAsync(Options, CancellationToken.None, handler);

        status.Available.ShouldBeFalse();
        status.ModelInstalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_ErrorStatus_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, """{"error":"boom"}"""));

        var status = await OllamaStatusProbe.ProbeAsync(Options, CancellationToken.None, handler);

        status.Available.ShouldBeFalse();
        status.ModelInstalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_MalformedTagsJson_ReturnsAvailableWithoutModel()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "not json"));

        var status = await OllamaStatusProbe.ProbeAsync(Options, CancellationToken.None, handler);

        status.Available.ShouldBeTrue();
        status.ModelInstalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_CancelledToken_PropagatesCancellation()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => OllamaStatusProbe.ProbeAsync(Options, cts.Token, handler));
    }

    [Fact]
    public async Task Probe_StalledIpv6Loopback_FallsThroughToIpv4Listener()
    {
        var handler = new DualAddressStubHandler();

        var status = await OllamaStatusProbe.ProbeAsync(Options, CancellationToken.None, handler);

        status.Available.ShouldBeTrue();
        status.ModelInstalled.ShouldBeTrue();
        handler.Hosts.ShouldHaveSingleItem().ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task Probe_StalledHandler_ReturnsUnavailableWithinBudget()
    {
        var handler = new StalledHttpMessageHandler();

        var status = await OllamaStatusProbe.ProbeAsync(
            Options, TimeSpan.FromMilliseconds(200), CancellationToken.None, handler);

        status.Available.ShouldBeFalse();
        status.ModelInstalled.ShouldBeFalse();
    }

    private sealed class StalledHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new HttpRequestException("probe stalled past the budget");
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

            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[{"name":"nomic-embed-text:latest"}]}""");
        }
    }
}
