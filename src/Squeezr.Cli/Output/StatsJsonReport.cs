using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public sealed record StatsJsonReport(
    int TotalRequests,
    IReadOnlyList<ToolStats> ByTool,
    StatsJsonReport.SavingsJson Savings,
    CacheStats Cache,
    StatsJsonReport.PeriodJson Period,
    IReadOnlyList<StatsJsonReport.AgentJson> Agents,
    IReadOnlyList<StatsJsonReport.SessionJson> Sessions)
{
    public sealed record SavingsJson(
        long Actual,
        long Counterfactual,
        long Saved,
        double Ratio,
        int UnknownFiles);

    public sealed record PeriodJson(DateTime? From, DateTime? To, int Records);

    public sealed record AgentJson(string Client, string? ClientVersion, int Count, DateTime LastSeen);

    public sealed record SessionJson(
        string SessionId,
        string Client,
        int Count,
        DateTime LastSeen,
        IReadOnlyList<string> TopTools);

    public static StatsJsonReport From(QueryStatsSummary summary) => new(
        summary.TotalRequests,
        summary.ByTool,
        new SavingsJson(
            summary.Savings.ActualTokens,
            summary.Savings.CounterfactualTokens,
            summary.Savings.SavedTokens,
            summary.Savings.Ratio,
            summary.Savings.UnknownFiles),
        summary.Cache,
        new PeriodJson(summary.From, summary.To, summary.Records),
        summary.Agents.Select(a => new AgentJson(a.Client, a.ClientVersion, a.Count, a.LastSeen)).ToList(),
        summary.Sessions
            .Select(s => new SessionJson(s.SessionId, s.Client, s.Count, s.LastSeen, s.TopTools))
            .ToList());
}
