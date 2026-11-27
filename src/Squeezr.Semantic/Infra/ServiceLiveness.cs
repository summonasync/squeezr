namespace Squeezr.Semantic.Infra;

public sealed record ServiceLiveness(ServiceLiveness.Outcome Kind, string? Detail)
{
    public enum Outcome
    {
        // An HTTP response arrived - the endpoint is live regardless of the status code.
        Answered,

        // The connection was refused after the retry budget: nothing is listening on the endpoint.
        Refused,

        // The attempt ended in a timeout after the retry budget: liveness is indeterminate.
        TimedOut,

        // Any other transport failure (TLS junk, reset mid-read, unreachable host) after the retry budget.
        TransportError
    }

    public static ServiceLiveness Answered(string? detail) => new(Outcome.Answered, detail);

    public static ServiceLiveness Refused() => new(Outcome.Refused, null);

    public static ServiceLiveness TimedOut() => new(Outcome.TimedOut, null);

    public static ServiceLiveness TransportError(string detail) => new(Outcome.TransportError, detail);
}
