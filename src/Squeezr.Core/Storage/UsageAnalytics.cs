namespace Squeezr.Core.Storage;

public sealed record WorkspaceUsage(
    string StoreKey,
    string RootPath,
    int Records,
    bool Stale,
    IReadOnlyList<AgentStats> Agents);

public sealed record SessionAdoption(int McpSessions, int HookSessions)
{
    public double? Ratio => HookSessions == 0 ? null : Math.Round((double)McpSessions / HookSessions, 2);
}

public sealed record UsageTotals(int Records, IReadOnlyList<AgentStats> Agents, SessionAdoption? Sessions = null);

public sealed record UsagePeriod(DateTime? From, DateTime? To);

public sealed record UsageRollup(
    IReadOnlyList<WorkspaceUsage> Workspaces,
    UsageTotals Totals,
    UsagePeriod Period);

public static class UsageAnalytics
{
    public static UsageRollup Rollup(
        IReadOnlyList<(WorkspaceRegistryEntry Workspace, IReadOnlyList<QueryTelemetryEntry> Entries)> workspaces,
        IReadOnlySet<string> staleKeys,
        string? agent = null,
        string? workspaceStoreKey = null,
        DateTime? since = null)
    {
        var blocks = new List<WorkspaceUsage>();
        var filteredAll = new List<QueryTelemetryEntry>();

        foreach (var (workspace, entries) in workspaces)
        {
            if (workspaceStoreKey is not null &&
                !string.Equals(workspace.StoreKey, workspaceStoreKey, StringComparison.Ordinal))
            {
                continue;
            }

            var filtered = Filter(entries, agent, since);
            if (filtered.Count == 0)
            {
                continue;
            }

            filteredAll.AddRange(filtered);
            blocks.Add(new WorkspaceUsage(
                workspace.StoreKey,
                workspace.RootPath,
                filtered.Count,
                staleKeys.Contains(workspace.StoreKey),
                QueryTelemetrySummary.AggregateAgents(filtered)));
        }

        return new UsageRollup(
            blocks,
            new UsageTotals(
                filteredAll.Count,
                QueryTelemetrySummary.AggregateAgents(filteredAll),
                CountSessions(filteredAll)),
            new UsagePeriod(
                filteredAll.Count == 0 ? null : filteredAll.Min(e => e.Ts),
                filteredAll.Count == 0 ? null : filteredAll.Max(e => e.Ts)));
    }

    // hook lines are the read-side sessions; unattributed ids join neither side, so the ratio never inflates
    private static SessionAdoption CountSessions(IReadOnlyList<QueryTelemetryEntry> entries)
    {
        var mcp = entries
            .Where(e => !string.Equals(e.Client, "hook", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.SessionId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Count();
        var hook = entries
            .Where(e => string.Equals(e.Client, "hook", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.SessionId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Count();
        return new SessionAdoption(mcp, hook);
    }

    private static IReadOnlyList<QueryTelemetryEntry> Filter(
        IReadOnlyList<QueryTelemetryEntry> entries, string? agent, DateTime? since) =>
        entries
            .Where(e =>
                (agent is null || string.Equals(e.Client ?? "unknown", agent, StringComparison.OrdinalIgnoreCase)) &&
                (since is null || e.Ts >= since.Value))
            .ToList();
}
