namespace Squeezr.Core.Indexing.Watch;

public static class WatchLiveness
{
    // The heartbeat proves a process lives, never that its work moves: the 2026-10-03 wedge beat every
    // 5s for 95 minutes while its pass sat blocked on a file open. The pass stamp is the second, honest half.
    public static WatchLivenessVerdict Classify(
        DaemonHeartbeat? heartbeat,
        PassPhaseStamp? pass,
        DateTime utcNow,
        Func<int, bool>? isProcessAlive = null)
    {
        if (heartbeat is null || !DaemonHeartbeatReader.IsAlive(heartbeat, utcNow, isProcessAlive))
        {
            return new(WatchLivenessState.Dead, "no live heartbeat for this store");
        }

        if (pass is null || pass.Pid != heartbeat.Pid)
        {
            return new(
                WatchLivenessState.Idle,
                $"heartbeat fresh (last beat {heartbeat.LastBeatAt:u}), no pass in flight from pid {heartbeat.Pid}");
        }

        return pass.Phase switch
        {
            PassPhaseStamp.StuckPhase => new(
                WatchLivenessState.Stuck,
                $"pass started {pass.StartedAt:u} overran its budget and is stuck; the heartbeat still beats " +
                $"(last beat {heartbeat.LastBeatAt:u}) but no progress has been recorded since {pass.LastProgressAt:u}"),
            PassPhaseStamp.RunningPhase => new(
                WatchLivenessState.Working,
                $"pass over {pass.BatchSize} changed path(s) started {pass.StartedAt:u}, " +
                $"last progress {pass.LastProgressAt:u}"),
            // not a pass in flight and not a wedge: a shutdown cut it short, so the store is owed one
            // reconciliation pass. Naming this apart keeps the newcomer from reading it as "not recognized"
            PassPhaseStamp.InterruptedPhase => new(
                WatchLivenessState.Idle,
                $"no pass in flight: the pass started {pass.StartedAt:u} was cut short by a shutdown and " +
                "never reported done, so the next start owes this store a reconciliation pass"),
            _ => new(
                WatchLivenessState.Idle,
                $"heartbeat fresh (last beat {heartbeat.LastBeatAt:u}), pass phase '{pass.Phase}' not recognized")
        };
    }
}
