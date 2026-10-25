namespace Squeezr.Core.Indexing.Watch;

public enum WatchIndexerState
{
    /// <summary>No claim marker at all — nothing has tried to own the watch loop.</summary>
    None,

    /// <summary>Marker present, its beat inside the stale window under a live pid on this host.</summary>
    Alive,

    /// <summary>Marker present and its pid alive, but the beat aged past the stale window: holds the store without indexing it.</summary>
    Wedged,

    /// <summary>The owning pid is gone (or the foreign window has expired): the claim is reclaimable, no indexer runs.</summary>
    Gone,

    /// <summary>Marker names another host inside the foreign window: the indexer runs where this machine cannot see it.</summary>
    Foreign,
}

// One read of a store answering the watch question a client or an operator actually asks: is the loop running,
// who owns it, and how many other live processes are attached. Ownership is the claim marker's answer alone —
// this only names what the marker and the beats say, it decides nothing and takes over nothing.
public sealed record WatchStoreHealth(
    WatchIndexerState State,
    int? OwnerPid,
    string? OwnerKind,
    string? OwnerHost,
    int Observers)
{
    public bool IndexerRunning => State is WatchIndexerState.Alive or WatchIndexerState.Foreign;

    public static WatchStoreHealth Probe(
        string storeDir,
        DateTime utcNow,
        Func<int, bool>? isProcessAlive = null,
        WatchOwnerMarker? owner = null)
    {
        var probe = isProcessAlive ?? DaemonHeartbeatReader.IsProcessAlive;
        owner ??= WatchOwnerMarker.TryRead(storeDir);
        var observers = 0;
        foreach (var beat in DaemonHeartbeatReader.TryReadAll(storeDir))
        {
            if (DaemonHeartbeatReader.IsAlive(beat, utcNow, probe) && (owner is null || !owner.OwnsBeat(beat)))
            {
                observers++;
            }
        }

        if (owner is null)
        {
            return new(WatchIndexerState.None, null, null, null, observers);
        }

        if (!string.Equals(owner.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                owner.IsPastForeignWindow(utcNow) ? WatchIndexerState.Gone : WatchIndexerState.Foreign,
                owner.Pid,
                owner.Kind,
                owner.Host,
                observers);
        }

        if (DaemonHeartbeatReader.IsAlive(owner.AsHeartbeat(), utcNow, probe))
        {
            return new(WatchIndexerState.Alive, owner.Pid, owner.Kind, owner.Host, observers);
        }

        return new(
            probe(owner.Pid) ? WatchIndexerState.Wedged : WatchIndexerState.Gone,
            owner.Pid,
            owner.Kind,
            owner.Host,
            observers);
    }
}
