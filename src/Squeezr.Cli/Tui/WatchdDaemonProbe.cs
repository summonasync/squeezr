using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Tui;

public sealed class WatchdDaemonProbe
{
    private readonly string _lockPath;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<int, DateTime?> _processStartUtc;

    public WatchdDaemonProbe() : this(WatchdPaths.LockPath)
    {
    }

    internal WatchdDaemonProbe(string lockPath) : this(lockPath, DaemonHeartbeatReader.IsProcessAlive)
    {
    }

    internal WatchdDaemonProbe(
        string lockPath,
        Func<int, bool> isProcessAlive,
        Func<int, DateTime?>? processStartUtc = null)
    {
        _lockPath = lockPath;
        _isProcessAlive = isProcessAlive;
        _processStartUtc = processStartUtc ?? WatchdLockOwner.ReadLiveStartUtc;
    }

    // The panel's one supervisor fact, read the same way the fleet column reads it: the lock names a pid, the
    // pid answers on this host, and the 2s start-time window keeps a daemon that came up seconds ago out of the
    // dead column. A claim that recorded no start time leaves the drift question unanswered, and the pid alone
    // answers it — absence is never read as a start time of now.
    public WatchdDaemonStatus Inspect()
    {
        if (WatchdLockOwner.TryRead(_lockPath) is not { Pid: > 0 } owner)
        {
            return WatchdDaemonStatus.NotRunning;
        }

        if (!owner.HeldOnThisHost)
        {
            return new WatchdDaemonStatus(true, owner.Pid);
        }

        if (!_isProcessAlive(owner.Pid))
        {
            return new WatchdDaemonStatus(false, owner.Pid);
        }

        return owner.CheckAgainstLiveStart(_processStartUtc(owner.Pid))
            is WatchdLockOwner.StartCheck.Agreed or WatchdLockOwner.StartCheck.NotRecorded
            ? new WatchdDaemonStatus(true, owner.Pid)
            : new WatchdDaemonStatus(false, owner.Pid);
    }
}
