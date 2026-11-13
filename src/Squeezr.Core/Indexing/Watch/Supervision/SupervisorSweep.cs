namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed record SupervisorSweep(
    DateTime StartedAtUtc,
    IReadOnlyList<StoreSupervision> Stores,
    string? Fault = null,
    OwnBinaryDrift? OwnDrift = null,
    string? StateWarning = null);
