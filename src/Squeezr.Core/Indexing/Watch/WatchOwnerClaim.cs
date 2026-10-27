using System.Diagnostics;

namespace Squeezr.Core.Indexing.Watch;

/// <summary>
/// Exclusive claim over one workspace store's watch loop, recorded as watch.owner inside the store dir.
/// IndexLock answers "who is indexing right now", this answers "who is allowed to index at all".
/// Exclusivity comes from a FileMode.CreateNew write, so concurrent acquirers cannot both believe they won;
/// the liveness of whoever is already in the file is decided by the same staleness rules the heartbeat uses,
/// and takeover needs the beat past the window *and* the owning pid gone. A takeover of an identified dead
/// owner is an adoption: the event lands on the store so fleet and status can say who unblocked the
/// deferred-embeddings queue, and the reap is an atomic compare-and-rename, so a session racing another
/// adopter can never delete the winner's fresh marker — two live sessions always end with exactly one owner.
/// A process that dies between the reap's rename and its delete leaves a .reap reservation behind; the next
/// successful claim sweeps those remnants and, when the reservation is all that is left of the displaced
/// owner, records the adoption the crashed reaper never got to write.
/// </summary>
public sealed class WatchOwnerClaim
{
    private readonly string _markerPath;
    private readonly string _storeDir;
    private readonly string _kind;
    private readonly string _host;
    private readonly int _pid;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<WatchOwnerMarker, bool>? _markerCreate;
    private readonly Func<WatchOwnerMarker?, bool>? _markerReap;
    private readonly Action<string>? _onWarning;

    public WatchOwnerClaim(
        string storeDir,
        string kind = DaemonHeartbeat.DefaultKind,
        Action<string>? onWarning = null)
        : this(storeDir, kind, null, null, onWarning, null, null)
    {
    }

    /// <summary>
    /// Seam-only shape: the clock, the pid probe, the pid this instance claims, the create step itself and the
    /// reap step itself, so several claimants can be played against one store inside one process. Kept off the
    /// public surface deliberately — a caller must not be able to hand in a permissive liveness predicate and
    /// win somebody else's store.
    /// </summary>
    internal WatchOwnerClaim(
        string storeDir,
        string kind,
        Func<DateTime>? utcNow,
        Func<int, bool>? isProcessAlive,
        Action<string>? onWarning,
        int? pid,
        Func<WatchOwnerMarker, bool>? markerCreate = null,
        Func<WatchOwnerMarker?, bool>? markerReap = null)
    {
        _markerPath = WatchOwnerMarker.PathFor(storeDir);
        _storeDir = storeDir;
        _kind = kind;
        _host = Environment.MachineName;
        _pid = pid ?? Environment.ProcessId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _markerCreate = markerCreate;
        _markerReap = markerReap;
        _onWarning = onWarning;
    }

    public string MarkerPath => _markerPath;

    public WatchOwnerOutcome TryAcquire()
    {
        var now = _utcNow();
        return Claim(NewClaim(now), null, now, reaping: false);
    }

    /// <summary>
    /// Keeps the claim warm on the owner's existing beat. A recorded owner rewrites its own marker; anyone else
    /// goes through the normal acquire rules, so a late beat cannot silently evict a newer owner.
    /// </summary>
    public WatchOwnerOutcome RefreshBeat()
    {
        var now = _utcNow();
        var (state, owner) = WatchOwnerMarkerFile.Read(_markerPath);
        if (state == WatchOwnerMarkerState.Present && owner!.IsOwnedBy(_pid, _host))
        {
            var beaten = owner with { LastBeatAt = now };
            try
            {
                WatchOwnerMarkerFile.Overwrite(_markerPath, beaten);
                return WatchOwnerOutcome.Acquired(beaten, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _onWarning?.Invoke($"owner marker beat failed: {ex.Message}");
            }
        }

        return Claim(NewClaim(now), null, now, reaping: false);
    }

    /// <summary>Graceful shutdown path: deletes the marker, and only while it still names us.</summary>
    public bool Release()
    {
        var (state, owner) = WatchOwnerMarkerFile.Read(_markerPath);
        if (state != WatchOwnerMarkerState.Present || !owner!.IsOwnedBy(_pid, _host))
        {
            return false;
        }

        if (WatchOwnerMarkerFile.TryDelete(_markerPath))
        {
            return true;
        }

        _onWarning?.Invoke("owner marker could not be released");
        return false;
    }

    public WatchOwnerMarker? ReadOwner()
    {
        var (state, marker) = WatchOwnerMarkerFile.Read(_markerPath);
        return state == WatchOwnerMarkerState.Present ? marker : null;
    }

    private WatchOwnerOutcome Claim(
        WatchOwnerMarker claim,
        WatchOwnerMarker? displaced,
        DateTime now,
        bool reaping)
    {
        if (reaping && !Reap(displaced))
        {
            // A refused reap has two faces: the marker changed hands between the evaluation and the delete
            // (someone live won it — evaluate the fresh read, recursion is bounded because every further
            // round needs a Present marker naming somebody new), or the file refuses to hand over our
            // displaced marker (or any marker at all — unreadable counts): an undeletable store is not
            // ours to take, so hold and say so instead of looping.
            var fresh = WatchOwnerMarkerFile.Read(_markerPath);
            if (fresh.State != WatchOwnerMarkerState.Present || fresh.Marker == displaced)
            {
                _onWarning?.Invoke("stale owner marker could not be removed");
                return WatchOwnerOutcome.Held(WatchOwnerStatus.StoreUnavailable, fresh.Marker);
            }

            return Evaluate(fresh, now);
        }

        bool created;
        try
        {
            created = _markerCreate is null
                ? WatchOwnerMarkerFile.TryCreate(_markerPath, claim)
                : _markerCreate(claim);
        }
        catch (DirectoryNotFoundException ex)
        {
            _onWarning?.Invoke($"owner marker cannot be written: {ex.Message}");
            return WatchOwnerOutcome.Held(WatchOwnerStatus.StoreUnavailable, null);
        }

        if (created)
        {
            if (reaping && displaced is { } dead)
            {
                RecordAdoption(claim, dead);
                WatchReapReservations.Sweep(_markerPath, _onWarning);
            }
            else
            {
                NameSweptRemnant(claim);
            }

            return WatchOwnerOutcome.Acquired(claim, displaced);
        }

        if (reaping)
        {
            var winner = ReadOwner();
            if (displaced is { } dead && winner is { } taker && !taker.IsOwnedBy(_pid, _host))
            {
                // The reap landed but the gap it opened went to another session; the taker is the adopter
                // as far as fleet and status are concerned, so leave the event here instead of letting the
                // displaced owner vanish from the story.
                RecordAdoption(taker, dead);
            }

            WatchReapReservations.Sweep(_markerPath, _onWarning);
            return WatchOwnerOutcome.Held(WatchOwnerStatus.RaceLost, winner);
        }

        return Evaluate(WatchOwnerMarkerFile.Read(_markerPath), now);
    }

    // A stale-and-gone owner is reaped only while the file still names exactly the marker that was evaluated;
    // a takeover whose displaced marker is unknown (unreadable reap) keeps the unconditional delete.
    private bool Reap(WatchOwnerMarker? displaced)
    {
        if (_markerReap is { } reap)
        {
            return reap(displaced);
        }

        return displaced is { } dead
            ? WatchOwnerMarkerFile.TryDeleteIf(_markerPath, dead)
            : WatchOwnerMarkerFile.TryDelete(_markerPath);
    }

    private void NameSweptRemnant(WatchOwnerMarker claim)
    {
        var swept = WatchReapReservations.Sweep(_markerPath, _onWarning);
        if (swept.Count == 0)
        {
            return;
        }

        var displaced = swept
            .OrderByDescending(remnant => remnant.LastBeatAt)
            .FirstOrDefault(remnant => !remnant.IsOwnedBy(_pid, _host));
        if (displaced is { } dead)
        {
            RecordAdoption(claim, dead);
        }
    }

    private void RecordAdoption(WatchOwnerMarker claim, WatchOwnerMarker dead)
    {
        try
        {
            WatchAdoptionEventFile.Write(_storeDir, new WatchAdoptionEvent(
                claim.Pid,
                claim.Host,
                claim.Kind,
                claim.StartedAt,
                dead.Pid,
                dead.Host,
                dead.StartedAt,
                dead.LastBeatAt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onWarning?.Invoke($"watch adoption could not be recorded: {ex.Message}");
        }
    }

    private WatchOwnerOutcome Evaluate(
        (WatchOwnerMarkerState State, WatchOwnerMarker? Marker) read,
        DateTime now)
    {
        if (read.State == WatchOwnerMarkerState.Unreadable)
        {
            var writtenAt = WatchOwnerMarkerFile.TryLastWrittenUtc(_markerPath);
            return writtenAt is DateTime stamp && now - stamp <= DaemonHeartbeat.StaleThreshold
                ? WatchOwnerOutcome.Held(WatchOwnerStatus.MarkerUnreadable, null)
                : TakeOver(null, now);
        }

        if (read.State == WatchOwnerMarkerState.Absent || read.Marker is not { } marker)
        {
            return WatchOwnerOutcome.Held(WatchOwnerStatus.RaceLost, null);
        }

        if (marker.IsOwnedBy(_pid, _host))
        {
            return WatchOwnerOutcome.Acquired(marker, null);
        }

        if (!string.Equals(marker.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            return marker.IsPastForeignWindow(now)
                ? TakeOver(marker, now)
                : WatchOwnerOutcome.Held(WatchOwnerStatus.ForeignOwnerHold, marker);
        }

        if (DaemonHeartbeatReader.IsAlive(marker.AsHeartbeat(), now, _isProcessAlive))
        {
            return WatchOwnerOutcome.Held(WatchOwnerStatus.OwnerHold, marker);
        }

        // Not alive by the heartbeat rule, but takeover needs both halves: a fresh beat from a pid that only
        // just died keeps the claim until the window passes (crash path relies on staleness), and a stale beat
        // from a pid that is still up is a wedged owner, not a free store.
        if (!marker.IsBeatStale(now))
        {
            return WatchOwnerOutcome.Held(WatchOwnerStatus.OwnerHold, marker);
        }

        return _isProcessAlive(marker.Pid)
            ? WatchOwnerOutcome.Held(WatchOwnerStatus.OwnerWedged, marker)
            : TakeOver(marker, now);
    }

    private WatchOwnerOutcome TakeOver(WatchOwnerMarker? displaced, DateTime now) =>
        Claim(NewClaim(now), displaced, now, reaping: true);

    private WatchOwnerMarker NewClaim(DateTime now) => new(_pid, _host, _kind, now, now);

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
