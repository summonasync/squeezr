using System.Net;
using System.Net.Sockets;
using Shouldly;
using Squeezr.Core.Infra;
using Squeezr.Semantic.Infra;

namespace Squeezr.Tests.Infra;

public class ServiceLivenessProbeTests
{
    [Fact]
    public async Task Probe_HttpResponse_ReturnsAnsweredWithStatusCodeDetail()
    {
        var probe = new ServiceLivenessProbe(CountingHandler.Respond(HttpStatusCode.OK));

        var liveness = await probe.ProbeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        liveness.Kind.ShouldBe(ServiceLiveness.Outcome.Answered);
        liveness.Detail.ShouldBe("200");
    }

    [Fact]
    public async Task Probe_ConnectionRefusedAfterRetry_ClassifiesRefusedWithTwoCalls()
    {
        var handler = CountingHandler.RefuseConnection();
        var probe = new ServiceLivenessProbe(handler);

        var liveness = await probe.ProbeAsync(ManagedServiceProbe.Qdrant, CancellationToken.None);

        liveness.Kind.ShouldBe(ServiceLiveness.Outcome.Refused);
        handler.Calls.ShouldBe(1 + ServiceLivenessProbe.RetryCount);
    }

    [Fact]
    public async Task Probe_TimeoutAfterRetry_ClassifiesTimedOutWithTwoCalls()
    {
        var handler = CountingHandler.Timeout();
        var probe = new ServiceLivenessProbe(handler);

        var liveness = await probe.ProbeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        liveness.Kind.ShouldBe(ServiceLiveness.Outcome.TimedOut);
        handler.Calls.ShouldBe(1 + ServiceLivenessProbe.RetryCount);
    }

    [Fact]
    public async Task Probe_TransportErrorAfterRetry_ClassifiesTransportError()
    {
        var handler = CountingHandler.TransportError("tls junk");
        var probe = new ServiceLivenessProbe(handler);

        var liveness = await probe.ProbeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        liveness.Kind.ShouldBe(ServiceLiveness.Outcome.TransportError);
        liveness.Detail.ShouldContain("tls junk");
    }

    [Fact]
    public async Task Probe_FirstAttemptTimesOut_SecondAnswerSolvesAsAnswered()
    {
        var handler = CountingHandler.TimeoutThenRespond(HttpStatusCode.OK);
        var probe = new ServiceLivenessProbe(handler);

        var liveness = await probe.ProbeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        liveness.Kind.ShouldBe(ServiceLiveness.Outcome.Answered);
        handler.Calls.ShouldBe(1 + ServiceLivenessProbe.RetryCount);
    }

    [Fact]
    public async Task Probe_CallerCancellation_PropagatesInsteadOfClassification()
    {
        var probe = new ServiceLivenessProbe(CountingHandler.Respond(HttpStatusCode.OK));
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => probe.ProbeAsync(ManagedServiceProbe.Ollama, caller.Token));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        private readonly int respondFromCall;

        private CountingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> respond, int respondFromCall)
        {
            this.respond = respond;
            this.respondFromCall = respondFromCall;
        }

        public int Calls { get; private set; }

        public static CountingHandler Respond(HttpStatusCode status) =>
            new(_ => new HttpResponseMessage(status), respondFromCall: 1);

        public static CountingHandler RefuseConnection() =>
            new(ThrowConnectionRefused, respondFromCall: 1);

        public static CountingHandler Timeout() =>
            new(ThrowTimeout, respondFromCall: 1);

        public static CountingHandler TransportError(string message) =>
            new(_ => throw new HttpRequestException(message), respondFromCall: 1);

        public static CountingHandler TimeoutThenRespond(HttpStatusCode status) =>
            new(_ => new HttpResponseMessage(status), respondFromCall: 2);

        private static HttpResponseMessage ThrowConnectionRefused(HttpRequestMessage request) =>
            throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused));

        private static HttpResponseMessage ThrowTimeout(HttpRequestMessage request) =>
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Calls >= respondFromCall ? respond(request) : ThrowTimeout(request));
        }
    }
}
