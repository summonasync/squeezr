using System.Net;
using System.Net.Sockets;
using Shouldly;
using Spectre.Console;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;

namespace Squeezr.Tests.Cli;

// T3 honest-surface pins: the status render distinguishes the four probe states instead of a bare
// compose table, down never claims an external instance was stopped, and the start hint states the
// adoption semantics. Everything runs through injected fakes - no real sockets.
public sealed class ServicesSurfacesHonestyTests
{
    [Fact]
    public void StatusRender_Absent_ShowsStartHintPerService()
    {
        var (report, probes) = Report(
            Absent(ManagedServiceProbe.Ollama),
            Absent(ManagedServiceProbe.Qdrant));
        var output = Render(report, probes);

        output.ShouldContain("ollama: absent — start it with: squeezr services up");
        output.ShouldContain("qdrant: absent — start it with: squeezr services up");
        output.ShouldNotContain("start-failed");
    }

    [Fact]
    public void StatusRender_External_NamesTheOwnerAndNeverRecommendsADuplicate()
    {
        var (report, probes) = Report(
            External(ManagedServiceProbe.Ollama, owner: "bobai-daily-assistant-ollama-1"),
            External(ManagedServiceProbe.Qdrant, owner: null));
        var output = Render(report, probes);

        output.ShouldContain("external — served by bobai-daily-assistant-ollama-1");
        output.ShouldContain("squeezr will not duplicate it");
        output.ShouldNotContain("start it with: squeezr services up");
    }

    [Fact]
    public void StatusRender_Unknown_ShowsTheManualCheckHint()
    {
        var (report, probes) = Report(
            Unknown(ManagedServiceProbe.Ollama),
            Unknown(ManagedServiceProbe.Qdrant));
        var output = Render(report, probes);

        output.ShouldContain("unknown — the probe failed after retry");
        output.ShouldContain("check the endpoint manually");
        output.ShouldNotContain("start it with: squeezr services up");
    }

    [Fact]
    public void StatusRender_Managed_ShowsTheManagedState()
    {
        var (report, probes) = Report(
            Managed(ManagedServiceProbe.Ollama),
            Managed(ManagedServiceProbe.Qdrant));
        var output = Render(report, probes);

        output.ShouldContain("managed (our compose project)");
        output.ShouldNotContain("start it with: squeezr services up");
    }

    [Fact]
    public void DownReportStillAnswering_LiveExternal_NamesItAsNotOurs()
    {
        var probe = new ServiceLivenessProbe(new AnswerOllamaAndQdrantHandler());
        var buffer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(buffer) });

        ServicesDownCommand.ReportStillAnsweringAsync(console, probe, CancellationToken.None)
            .GetAwaiter().GetResult();

        var text = buffer.ToString();
        text.ShouldContain("ollama is still answering on");
        text.ShouldContain("qdrant is still answering on");
        text.ShouldContain("that instance was not");
        text.ShouldContain("left untouched");
    }

    [Fact]
    public async Task DownReportStillAnswering_DeadEndpoints_ReportsNothing()
    {
        var probe = new ServiceLivenessProbe(new RefuseHandler());
        var buffer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(buffer) });

        await ServicesDownCommand.ReportStillAnsweringAsync(console, probe, CancellationToken.None);

        buffer.ToString().ShouldNotContain("still answering");
    }

    private static (ServicesStatusReport Report, IReadOnlyList<ExternalServiceState> Probes) Report(
        params ExternalServiceState[] probes) =>
        (new ServicesStatusReport(
            "/tmp/x/infra/docker-compose.yml",
            "squeezr",
            Rocm: false,
            ComposeVersion: "v2.39.0",
            Services: []), probes);

    private static ExternalServiceState Absent(ManagedServiceProbe probe) =>
        new(probe, ServiceProbeState.Absent, null, null);

    private static ExternalServiceState External(ManagedServiceProbe probe, string? owner) =>
        new(probe, ServiceProbeState.External, owner, null);

    private static ExternalServiceState Unknown(ManagedServiceProbe probe) =>
        new(probe, ServiceProbeState.Unknown, null, null);

    private static ExternalServiceState Managed(ManagedServiceProbe probe) =>
        new(probe, ServiceProbeState.Managed, "squeezr compose project", null);

    private static string Render(ServicesStatusReport report, IReadOnlyList<ExternalServiceState> probes)
    {
        var buffer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(buffer) });
        ServicesStatusCommand.RenderHuman(console, report, probes);
        return buffer.ToString();
    }

    private sealed class AnswerOllamaAndQdrantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.Contains("version")
                ? """{"version":"0.34.1"}"""
                : """{"models":[{"name":"nomic-embed-text:latest"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class RefuseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }
}
