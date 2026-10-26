using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

// 075's writer-presence line: a live watcher on the store is the reason a freshness verdict can flip
// to fresh between an agent's calls — the agent hears it in the same answer that carries the verdicts.
// No claim marker (or no live owner) means nothing to say: the absence case is NoIndexerNotice's, not this one's.
internal static class StoreWriterNotice
{
    public static string? For(string workspaceRoot)
    {
        var storeDir = SqueezrPaths.StoreDir(workspaceRoot);
        var marker = WatchOwnerMarker.TryRead(storeDir);
        if (marker is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var health = WatchStoreHealth.Probe(storeDir, now, owner: marker);
        if (health.State is not WatchIndexerState.Alive)
        {
            return null;
        }

        var beatAge = Math.Max(0, (long)(now - AsUtc(marker.LastBeatAt)).TotalSeconds);
        return $"active writer on this store: pid {health.OwnerPid} ({health.OwnerKind}), watch beat {beatAge}s old " +
               "— freshness verdicts may flip to fresh as it lands its pass";
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
