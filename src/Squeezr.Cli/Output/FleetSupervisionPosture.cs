using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Output;

// The one supervision word a store answers with, from facts this machine can read: the desire file,
// the claim probe and whether the watchd lock is held. Wedged leads — a stale claim held by a live
// pid is the pathology every other cell would otherwise cosmeticize. A live owner beside a running
// watchd reads as supervised only when it is the watch kind on this host; a serve --watch belongs to
// its MCP client and a foreign claim lives where this machine cannot reach, so both stay managed
// elsewhere no matter who runs a supervisor here.
//
// "stopped" and "idle (no supervisor)" are the two faces of nothing running: the first is a wish the
// operator wrote, the second is a wish nobody is left to carry out. Collapsing them cost an operator
// an afternoon reading a fleet that wanted watching with no watchd alive (113/R5), so the words stay
// distinct even though every live fact behind them is the same.
public static class FleetSupervisionPosture
{
    public const string Supervised = "supervised";
    public const string ManagedElsewhere = "managed elsewhere";
    public const string Wedged = "wedged";
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Idle = "idle (no supervisor)";

    public static string Resolve(WatchDesire desired, WatchStoreHealth health, bool watchdRunning) =>
        health.State == WatchIndexerState.Wedged ? Wedged
        : health.IndexerRunning ? ResolveLiveOwner(desired, health, watchdRunning)
        : desired == WatchDesire.Stopped ? Stopped
        : watchdRunning ? Starting
        : Idle;

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
