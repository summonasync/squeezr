namespace Squeezr.Core.Indexing.Watch;

public readonly record struct WatchOwnerOutcome(
    WatchOwnerStatus Status,
    WatchOwnerMarker? Owner,
    WatchOwnerMarker? TookOverFrom)
{
    public bool IsOwner => Status == WatchOwnerStatus.Acquired;

    public static WatchOwnerOutcome Acquired(WatchOwnerMarker marker, WatchOwnerMarker? tookOverFrom) =>
        new(WatchOwnerStatus.Acquired, marker, tookOverFrom);

    public static WatchOwnerOutcome Held(WatchOwnerStatus status, WatchOwnerMarker? owner) =>
        new(status, owner, null);
}
