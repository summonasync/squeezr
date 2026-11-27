using System.Net;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Embeddings;

public class OllamaExecutorProbeTests
{
    private const string PsPath = "/api/ps";

    private static OllamaOptions Options => new()
    {
        Endpoint = "http://localhost:11434",
        Model = "nomic-embed-text",
    };

    [Fact]
    public async Task Probe_ProcessorNamesGpu_MapsToGpu()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","processor":"100% GPU","size":343597384,"size_vram":343597384}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("gpu");
        executor.Reason.ShouldBeNull();
        handler.Requests.ShouldHaveSingleItem().Path.ShouldBe(PsPath);
    }

    [Fact]
    public async Task Probe_ProcessorNamesCpu_MapsToCpu()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","processor":"cpu","size":343597384,"size_vram":0}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("cpu");
        executor.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Probe_ProcessorSplitAcrossDevices_IsUnknownWithReason()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","processor":"48% CPU / 52% GPU","size":1000,"size_vram":520}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldNotBeNull();
    }

    [Fact]
    public async Task Probe_ProcessorAbsentButWholeModelInVram_MapsToGpu()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","model":"nomic-embed-text:latest","size":343597384,"size_vram":343597384,"details":{"family":"nomic-bert"}}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("gpu");
        executor.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Probe_NothingResidentInVram_MapsToCpu()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","size":343597384,"size_vram":0}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("cpu");
        executor.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Probe_PartiallyResidentInVram_IsUnknownWithTheResidentShare()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","size":1000,"size_vram":520}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("52%");
    }

    [Fact]
    public async Task Probe_SizeVramExactlyAtTheThreshold_IsNotGpu()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","size":1000,"size_vram":990}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("99%");
    }

    [Fact]
    public async Task Probe_SizeVramJustAboveTheThreshold_MapsToGpu()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","size":1000,"size_vram":995}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("gpu");
        executor.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Probe_ExactTaggedModel_MatchesItsOwnEntry_NotAnotherTagOfTheSameBase()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                {"models":[
                    {"name":"nomic-embed-text:latest","size":1000,"size_vram":0},
                    {"name":"nomic-embed-text:q4_0","size":1000,"size_vram":1000}]}
                """));

        var executor = await OllamaExecutorProbe.ProbeAsync(
            Options with { Model = "nomic-embed-text:q4_0" }, CancellationToken.None, handler);

        executor.Device.ShouldBe("gpu", "the verdict comes from the configured tag's own entry, not the first tag of the base name");
    }

    [Fact]
    public async Task Probe_ExactTaggedModelMissingFromTheList_IsUnknownEvenWhenOtherTagsAreLoaded()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","size":1000,"size_vram":1000}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(
            Options with { Model = "nomic-embed-text:q4_0" }, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("q4_0");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Probe_ErrorStatus_IsUnknownWithTheStatus(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(status, """{"error":"boom"}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain(((int)status).ToString());
    }

    [Fact]
    public async Task Probe_EndpointUnreachable_IsUnknownWithReason()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldNotBeNull();
    }

    [Fact]
    public async Task Probe_TimedOut_IsUnknownWithTimeoutReason()
    {
        var handler = new StubHttpMessageHandler(new TaskCanceledException());

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("timed out");
    }

    [Fact]
    public async Task Probe_MalformedPayload_IsUnknownWithReason()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "not json"));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("malformed");
    }

    [Fact]
    public async Task Probe_ConfiguredModelNotLoaded_IsUnknownWithReason()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"llama3:latest","size":1000,"size_vram":1000}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("nomic-embed-text");
    }

    [Fact]
    public async Task Probe_NoModelsLoaded_IsUnknownWithReason()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldNotBeNull();
    }

    [Fact]
    public async Task Probe_EntryWithoutSizeFields_IsUnknownWithReason()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest"}]}"""));

        var executor = await OllamaExecutorProbe.ProbeAsync(Options, CancellationToken.None, handler);

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("size");
    }

    [Fact]
    public async Task Probe_CancelledToken_PropagatesCancellation()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => OllamaExecutorProbe.ProbeAsync(Options, cts.Token, handler));
    }
}
