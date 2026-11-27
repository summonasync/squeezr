using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Lite.Mcp;

// Lite runs no watcher of its own, but a full squeezr watcher can still be landing passes on the same store —
// the two products share the on-disk format, so the honest answer names the writer instead of pretending the
// index stands still.
internal static class WriterNotice
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
               "— rows may already be behind it; 'sqzrlite index' reconciles the store with the tree";
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
