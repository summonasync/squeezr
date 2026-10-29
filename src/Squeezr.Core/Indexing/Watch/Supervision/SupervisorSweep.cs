namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed record SupervisorSweep(DateTime StartedAtUtc, IReadOnlyList<StoreSupervision> Stores, string? Fault = null);
