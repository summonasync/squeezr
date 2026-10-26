namespace Squeezr.Core.Infra;

public sealed record ExternalServiceState(
    ManagedServiceProbe Probe,
    ServiceProbeState State,
    string? Owner,
    string? Detail);
