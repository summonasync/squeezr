namespace Squeezr.Core.Storage;

public static class UsageRollupLoader
{
    public static UsageRollup Load(string? agent = null, string? workspaceStoreKey = null, DateTime? since = null)
    {
        var staleKeys = StaleStoreKeys();
        var inputs = WorkspaceRegistry.Load()
            .Select(entry => (Workspace: entry, Entries: QueryTelemetry.ReadAll(entry.RootPath)))
            .ToList();
        return UsageAnalytics.Rollup(inputs, staleKeys, agent, workspaceStoreKey, since);
    }

    // The fleet's definition of "registered but unreachable": one helper so the rollup that displays
    // the fleet and the clear that wipes it can never disagree about which stores count.
    internal static HashSet<string> StaleStoreKeys()
    {
        return OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .ToHashSet(StringComparer.Ordinal);
    }
}
