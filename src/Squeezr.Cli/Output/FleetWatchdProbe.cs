using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Output;

public static class FleetWatchdProbe
{
    private static readonly FleetWatchdState NotRunning = new(false, null);

    public static FleetWatchdState Probe() =>
        Probe(WatchdPaths.LockPath, DaemonHeartbeatReader.IsProcessAlive);

    internal static FleetWatchdState Probe(string lockPath, Func<int, bool> isProcessAlive)
    {
        var owner = WatchdLockOwner.TryRead(lockPath);
        if (owner is not { Pid: > 0 } held)
        {
            return NotRunning;
        }

        if (!held.HeldOnThisHost)
        {
            return new FleetWatchdState(true, held.Pid);
        }

        if (!isProcessAlive(held.Pid))
        {
            return NotRunning;
        }

        return held.CheckAgainstLiveStart(WatchdLockOwner.ReadLiveStartUtc(held.Pid))
            is WatchdLockOwner.StartCheck.Agreed or WatchdLockOwner.StartCheck.NotRecorded
            ? new FleetWatchdState(true, held.Pid)
            : NotRunning;
    }
}
