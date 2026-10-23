using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp.Onboarding;

// The store half of `mcp doctor`: one read of the watch claim and the beats roster for the workspace the doctor
// is standing in. A workspace nothing registered answers null — there is no store to name an indexer of — and a
// resolved store always answers with a state, including the none case this exists to make visible.
public static class StoreWatchProbe
{
    public static StoreWatchDiagnostics? TryProbe(string workspaceRoot)
    {
        if (!WorkspaceRegistry.TryGetStoreDir(workspaceRoot, out var storeDir))
        {
            return null;
        }

        var entry = WorkspaceRegistry.FindByRootPath(workspaceRoot);
        if (entry is null)
        {
            return null;
        }

        var health = WatchStoreHealth.Probe(storeDir, DateTime.UtcNow);
        return StoreWatchDiagnostics.From(health, entry.Name, entry.StoreKey);
    }
}
