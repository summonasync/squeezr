using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Lite.Commands;

// The hook-side guard against double indexing: a live beat of either claim kind means a watcher pass owns
// this store's healing, and a manual index run would only fight it for the lock. The claim marker is not
// the evidence here — ownership says who may index at all, the heartbeat says who is doing it now.
internal static class WatcherOwnership
{
    public static readonly string[] ClaimKinds = [DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind];

    public static bool LiveOnStore(string storeDir) => OwnerPid(storeDir) is not null;

    public static int? OwnerPid(string storeDir)
    {
        var now = DateTime.UtcNow;
        foreach (var kind in ClaimKinds)
        {
            var beat = DaemonHeartbeatReader.TryRead(storeDir, kind);
            if (beat is not null && DaemonHeartbeatReader.IsAlive(beat, now))
            {
                return beat.Pid;
            }
        }

        return null;
    }
}
