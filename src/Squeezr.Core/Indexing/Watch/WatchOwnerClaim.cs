using System.Diagnostics;

namespace Squeezr.Core.Indexing.Watch;

/// <summary>
/// Exclusive claim over one workspace store's watch loop, recorded as watch.owner inside the store dir.
/// IndexLock answers "who is indexing right now", this answers "who is allowed to index at all".
/// Exclusivity comes from a FileMode.CreateNew write, so concurrent acquirers cannot both believe they won;
/// the liveness of whoever is already in the file is decided by the same staleness rules the heartbeat uses,
/// and takeover needs the beat past the window *and* the owning pid gone.
/// </summary>
public sealed class WatchOwnerClaim
{
    private readonly string _markerPath;
    private readonly string _kind;
    private readonly string _host;
    private readonly int _pid;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<WatchOwnerMarker, bool>? _markerCreate;
    private readonly Action<string>? _onWarning;

    public WatchOwnerClaim(
        string storeDir,
        string kind = DaemonHeartbeat.DefaultKind,
        Action<string>? onWarning = null)
        : this(storeDir, kind, null, null, onWarning, null, null)
    {
    }

    /// <summary>
    /// Seam-only shape: the clock, the pid probe, the pid this instance claims and the create step itself, so several
    /// claimants can be played against one store inside one process. Kept off the public surface deliberately — a
    /// caller must not be able to hand in a permissive liveness predicate and win somebody else's store.
    /// </summary>
    internal WatchOwnerClaim(
        string storeDir,
        string kind,
        Func<DateTime>? utcNow,
        Func<int, bool>? isProcessAlive,
        Action<string>? onWarning,
        int? pid,
        Func<WatchOwnerMarker, bool>? markerCreate = null)
    {
        _markerPath = WatchOwnerMarker.PathFor(storeDir);
        _kind = kind;
        _host = Environment.MachineName;
        _pid = pid ?? Environment.ProcessId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _markerCreate = markerCreate;
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
        if (reaping && !WatchOwnerMarkerFile.TryDelete(_markerPath))
        {
            _onWarning?.Invoke("stale owner marker could not be removed");
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
            return WatchOwnerOutcome.Acquired(claim, displaced);
        }

        if (reaping)
        {
            return WatchOwnerOutcome.Held(WatchOwnerStatus.RaceLost, ReadOwner());
        }

        return Evaluate(WatchOwnerMarkerFile.Read(_markerPath), now);
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
