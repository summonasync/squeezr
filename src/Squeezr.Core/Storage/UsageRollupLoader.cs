namespace Squeezr.Core.Storage;

public static class UsageRollupLoader
{
    public static UsageRollup Load(string? agent = null, string? workspaceStoreKey = null, DateTime? since = null)
    {
        var registry = WorkspaceRegistry.Load();
        var inputs = registry
            .Select(entry => (Workspace: entry, Entries: QueryTelemetry.ReadAll(entry.RootPath)))
            .ToList();
        return UsageAnalytics.Rollup(inputs, agent, workspaceStoreKey, since);
    }
}
