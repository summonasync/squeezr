namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed record StoreSupervision(
    string StoreKey,
    string Name,
    string RootPath,
    WatchDesire Desire,
    WatchIndexerState IndexerState,
    int? OwnerPid,
    int? ManagedPid,
    SupervisionAction Action,
    string? Detail);
