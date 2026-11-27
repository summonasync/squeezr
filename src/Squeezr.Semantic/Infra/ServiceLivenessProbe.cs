using System.Net;
using System.Net.Sockets;

namespace Squeezr.Semantic.Infra;

public sealed class ServiceLivenessProbe
{
    public const int TimeoutSeconds = 5;
    public const int RetryCount = 1;

    private readonly HttpClient client;

    // The probe is a trust root for up/down decisions: an ambient HTTP_PROXY would let a proxy's
    // 502 read as "live" and a proxy's refusal read as "absent", so the default handler runs proxy
    // free. Tests inject a handler, not a prebuilt client.
    public ServiceLivenessProbe()
        : this(new SocketsHttpHandler { UseProxy = false })
    {
    }

    public ServiceLivenessProbe(HttpMessageHandler handler)
    {
        client = new HttpClient(handler);
    }

    public async Task<ServiceLiveness> ProbeAsync(ManagedServiceProbe probe, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutScope.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                using var response = await client.GetAsync(
                    new Uri(probe.Endpoint, probe.ProbePath),
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutScope.Token);
                return ServiceLiveness.Answered(((int)response.StatusCode).ToString());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= RetryCount)
                {
                    return ServiceLiveness.TimedOut();
                }
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= RetryCount)
                {
                    return Classify(ex);
                }
            }
        }
    }

    // A clean connection-refused is the one transport outcome that means "nothing is listening":
    // after the retry budget it is an honest absent. A refused-shaped ConnectionError can also mean
    // an unreachable network, so only a literal SocketException.ConnectionRefused counts - every
    // other transport failure stays indeterminate and must never degrade to absent.
    private static ServiceLiveness Classify(HttpRequestException ex) =>
        IsConnectionRefused(ex) ? ServiceLiveness.Refused() : ServiceLiveness.TransportError(ex.Message);

    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }

        return false;
    }
}
