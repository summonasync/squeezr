using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Shouldly;
using Squeezr.Core.Infra;

namespace Squeezr.Tests.Infra;

public class ExternalServiceStateSolverTests
{
    [Fact]
    public void Solve_OwnershipOurs_YieldsManagedEvenWhenNothingAnswers()
    {
        var probe = ManagedServiceProbe.Ollama;

        var state = ExternalServiceStateSolver.Solve(
            probe, ServiceLiveness.Refused(), ServiceOwnership.Ours);

        state.State.ShouldBe(ServiceProbeState.Managed);
        state.Owner.ShouldBe("squeezr compose project");
        state.Probe.ShouldBe(probe);
    }

    [Fact]
    public void Solve_AnsweredAndForeign_YieldsExternalWithNamedOwner()
    {
        var state = ExternalServiceStateSolver.Solve(
            ManagedServiceProbe.Ollama,
            ServiceLiveness.Answered("200"),
            ServiceOwnership.Foreign("bobai-daily-assistant-ollama-1"));

        state.State.ShouldBe(ServiceProbeState.External);
        state.Owner.ShouldBe("bobai-daily-assistant-ollama-1");
    }

    [Fact]
    public void Solve_AnsweredAndUnexplained_DegradesToExternalWithUnknownOwner()
    {
        var state = ExternalServiceStateSolver.Solve(
            ManagedServiceProbe.Qdrant,
            ServiceLiveness.Answered("200"),
            ServiceOwnership.Unexplained);

        state.State.ShouldBe(ServiceProbeState.External);
        state.Owner.ShouldBeNull();
    }

    [Fact]
    public void Solve_ProbeTimedOut_YieldsUnknownNeverAbsent()
    {
        var state = ExternalServiceStateSolver.Solve(
            ManagedServiceProbe.Ollama,
            ServiceLiveness.TimedOut(),
            ServiceOwnership.Unexplained);

        state.State.ShouldBe(ServiceProbeState.Unknown);
    }

    [Fact]
    public void Solve_ProbeTransportError_YieldsUnknownNeverAbsent()
    {
        var state = ExternalServiceStateSolver.Solve(
            ManagedServiceProbe.Qdrant,
            ServiceLiveness.TransportError("tls junk"),
            ServiceOwnership.Unexplained);

        state.State.ShouldBe(ServiceProbeState.Unknown);
    }

    [Fact]
    public void Solve_ConnectionRefused_YieldsAbsent()
    {
        var state = ExternalServiceStateSolver.Solve(
            ManagedServiceProbe.Ollama,
            ServiceLiveness.Refused(),
            ServiceOwnership.Unexplained);

        state.State.ShouldBe(ServiceProbeState.Absent);
        state.Owner.ShouldBeNull();
    }

    [Fact]
    public async Task SolveAsync_ServicesResolveIndependentlyThroughTheSamePrimitive()
    {
        var states = await ExternalServiceStateSolver.SolveAsync(
            [ManagedServiceProbe.Ollama, ManagedServiceProbe.Qdrant],
            new ServiceLivenessProbe(AnswerOllamaRefuseQdrant()),
            FakeOwnership.AnswersForeignFor("ollama"),
            CancellationToken.None);

        var ollama = states.Single(state => state.Probe.ServiceName == "ollama");
        ollama.State.ShouldBe(ServiceProbeState.External);
        ollama.Owner.ShouldBe("foreign-container-1");

        var qdrant = states.Single(state => state.Probe.ServiceName == "qdrant");
        qdrant.State.ShouldBe(ServiceProbeState.Absent);
    }

    [Fact]
    public async Task SolveAsync_ProbesThatDoNotAnswer_AttributionIsNeverConsulted()
    {
        var ownership = new CountingOwnership();
        var states = await ExternalServiceStateSolver.SolveAsync(
            [ManagedServiceProbe.Ollama, ManagedServiceProbe.Qdrant],
            new ServiceLivenessProbe(RefuseEverywhere()),
            ownership,
            CancellationToken.None);

        states.All(state => state.State == ServiceProbeState.Absent).ShouldBeTrue();
        ownership.Calls.ShouldBe(0);
    }

    private static HttpMessageHandler AnswerOllamaRefuseQdrant()
    {
        return new CountingHandler(request => request.RequestUri!.OriginalString.Contains("11434")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused)));
    }

    private static HttpMessageHandler RefuseEverywhere()
    {
        return new CountingHandler(_ => throw new HttpRequestException(
            "Connection refused", new SocketException((int)SocketError.ConnectionRefused)));
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class CountingOwnership : IServiceOwnershipSource
    {
        public int Calls { get; private set; }

        public Task<ServiceOwnership> AttributeAsync(
            ManagedServiceProbe probe, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ServiceOwnership.Foreign("should-never-be-consulted"));
        }
    }

    private sealed class FakeOwnership(Func<ManagedServiceProbe, ServiceOwnership> answers) : IServiceOwnershipSource
    {
        public static FakeOwnership AnswersForeignFor(string serviceName) =>
            new(probe => probe.ServiceName == serviceName
                ? ServiceOwnership.Foreign("foreign-container-1")
                : ServiceOwnership.Unexplained);

        public Task<ServiceOwnership> AttributeAsync(
            ManagedServiceProbe probe, CancellationToken cancellationToken) =>
            Task.FromResult(answers(probe));
    }
}
