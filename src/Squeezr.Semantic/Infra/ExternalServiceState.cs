namespace Squeezr.Semantic.Infra;

public sealed record ExternalServiceState(
    ManagedServiceProbe Probe,
    ServiceProbeState State,
    string? Owner,
    string? Detail);
