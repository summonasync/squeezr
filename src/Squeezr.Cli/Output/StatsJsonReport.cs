using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public sealed record StatsJsonReport(
    int TotalRequests,
    IReadOnlyList<ToolStats> ByTool,
    StatsJsonReport.SavingsJson Savings,
    CacheStats Cache,
    StatsJsonReport.PeriodJson Period)
{
    public sealed record SavingsJson(
        long Actual,
        long Counterfactual,
        long Saved,
        double Ratio,
        int UnknownFiles);

    public sealed record PeriodJson(DateTime? From, DateTime? To, int Records);

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
        new PeriodJson(summary.From, summary.To, summary.Records));
}
