namespace Squeezr.Core.Indexing.Watch;

public enum WatchOwnerStatus
{
    Acquired,

    /// <summary>Someone else owns the store and their beat is inside the stale window.</summary>
    OwnerHold,

    /// <summary>
    /// Beat older than the stale window while the owning pid is still alive: not healthy, but not reclaimable
    /// either — a wedged owner is 055's bounded-pass problem, not a licence to double-index.
    /// </summary>
    OwnerWedged,

    /// <summary>
    /// Marker names another host. Pid evidence is not usable across hosts, so it holds the store until
    /// <see cref="WatchOwnerMarker.ForeignHostStaleWindow"/> has passed.
    /// </summary>
    ForeignOwnerHold,

    /// <summary>Marker is corrupt or unreadable and has not aged past the stale window yet.</summary>
    MarkerUnreadable,

    /// <summary>Another contender created the marker between our reap of a stale one and our own create.</summary>
    RaceLost,

    /// <summary>The marker cannot be written at all — the store directory is gone or not writable.</summary>
    StoreUnavailable,
}
