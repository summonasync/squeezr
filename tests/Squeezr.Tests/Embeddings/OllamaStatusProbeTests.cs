using System.Net;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;

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
}
