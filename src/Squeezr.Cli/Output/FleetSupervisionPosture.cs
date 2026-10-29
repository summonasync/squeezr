using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Output;

// The one supervision word a store answers with, from facts this machine can read: the desire file,
// the claim probe and whether the watchd lock is held. Wedged leads — a stale claim held by a live
// pid is the pathology every other cell would otherwise cosmeticize. A live owner beside a running
// watchd reads as supervised only when it is the watch kind on this host; a serve --watch belongs to
// its MCP client and a foreign claim lives where this machine cannot reach, so both stay managed
// elsewhere no matter who runs a supervisor here.
public static class FleetSupervisionPosture
{
    public const string Supervised = "supervised";
    public const string ManagedElsewhere = "managed elsewhere";
    public const string Wedged = "wedged";
    public const string Stopped = "stopped";
    public const string Starting = "starting";

    public static string Resolve(WatchDesire desired, WatchStoreHealth health, bool watchdRunning) =>
        health.State == WatchIndexerState.Wedged ? Wedged
        : health.IndexerRunning ? ResolveLiveOwner(desired, health, watchdRunning)
        : desired == WatchDesire.Stopped ? Stopped
        : watchdRunning ? Starting
        : Stopped;

    private static string ResolveLiveOwner(WatchDesire desired, WatchStoreHealth health, bool watchdRunning)
    {
        if (desired == WatchDesire.Stopped)
        {
            return Stopped;
        }

        var supervised = watchdRunning
            && health.OwnerKind != DaemonHeartbeat.ServeWatchKind
            && string.Equals(health.OwnerHost, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        return supervised ? Supervised : ManagedElsewhere;
    }
}
