namespace Squeezr.Core.Storage;

public static class UsageRollupLoader
{
    public static UsageRollup Load(string? agent = null, string? workspaceStoreKey = null, DateTime? since = null)
    {
        var staleKeys = OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .ToHashSet(StringComparer.Ordinal);
        var inputs = WorkspaceRegistry.Load()
            .Select(entry => (Workspace: entry, Entries: QueryTelemetry.ReadAll(entry.RootPath)))
            .ToList();
        return UsageAnalytics.Rollup(inputs, staleKeys, agent, workspaceStoreKey, since);
    }
}
