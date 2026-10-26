using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class StatsCommand : Command<StatsCommand.Settings>
{
    private const int RetentionHintThreshold = 50_000;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--reset")]
        public bool Reset { get; set; }

        [CommandOption("--all")]
        public bool All { get; set; }

        [CommandOption("--yes")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.All && !settings.Reset)
            {
                throw new McpUsageException("--all requires --reset.");
            }

            if (settings.Reset && settings.All)
            {
                return UsageCommand.Clear(settings.Json, settings.Yes);
            }

            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            if (settings.Reset)
            {
                return Reset(workspaceRoot, settings.Json);
            }

            var summary = QueryTelemetrySummary.Summarize(QueryTelemetry.ReadAll(workspaceRoot), workspaceRoot);
            if (settings.Json)
            {
                JsonOutput.Write(StatsJsonReport.From(summary));
                return 0;
            }

            GuardOutput.WriteWithheld(summary.GuardWarnings);
            Render(AnsiConsole.Console, summary);
            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static int Reset(string workspaceRoot, bool json)
    {
        var removed = QueryTelemetry.Reset(workspaceRoot);

        if (json)
        {
            JsonOutput.Write(new { reset = true, recordsRemoved = removed });
        }
        else
        {
            Console.WriteLine($"Cleared {removed} telemetry record(s).");
        }

        return 0;
    }

    public static void Render(IAnsiConsole console, QueryStatsSummary summary)
    {
        if (summary.Records == 0)
        {
            console.MarkupLine("No query telemetry yet.");
            return;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Tool");
        table.AddColumn("Count");
        table.AddColumn("Avg tokens");
        table.AddColumn("Truncation");
        foreach (var tool in summary.ByTool)
        {
            table.AddRow(
                tool.Tool.EscapeMarkup(),
                tool.Count.ToString(),
                tool.AvgTokensUsed.ToString("F1", CultureInfo.InvariantCulture),
                tool.TruncationRate.ToString("P1", CultureInfo.InvariantCulture));
        }

        console.Write(table);

        var savings = summary.Savings;
        if (savings.BaselineApplies)
        {
            var ratio = savings.Ratio.ToString("F1", CultureInfo.InvariantCulture);
            console.MarkupLine(
                $"Savings: query cost {savings.ActualTokens} · whole-file baseline {savings.CounterfactualTokens} · " +
                $"[green]saved {savings.SavedTokens}[/] (×{ratio})");
        }
        else
        {
            console.MarkupLine(
                $"Savings: [grey]no whole-file baseline for this usage mix " +
                $"(query cost {savings.ActualTokens}; the baseline counts whole files read via expand only)[/]");
        }

        if (savings.UnknownFiles > 0)
        {
            console.MarkupLine(
                $"[grey]{savings.UnknownFiles} expanded file(s) not in the index, excluded from counterfactual.[/]");
        }

        var cache = summary.Cache;
        if (cache.SessionsWithCache > 0)
        {
            var plural = cache.EligibleQueries == 1 ? "query" : "queries";
            console.MarkupLine(
                $"Cache hit-rate: {cache.HitRate.ToString("P1", CultureInfo.InvariantCulture)} " +
                $"across {cache.SessionsWithCache} session(s) over {cache.EligibleQueries} cache-eligible {plural}");
        }
        else if (cache.EligibleQueries > 0)
        {
            var invalidation = cache.LastInvalidation is { } lastInvalidated
                ? $" — the index changed {cache.Invalidations} time(s), last at {lastInvalidated:yyyy-MM-dd HH:mm}"
                : string.Empty;
            console.MarkupLine(
                $"[grey]Cache hit-rate: no hits yet across {summary.Sessions.Count} recorded session(s){invalidation}[/]");
        }
        else
        {
            console.MarkupLine(
                "[grey]Cache hit-rate: no cache-eligible queries yet — recorded tools run outside the serve cache[/]");
        }

        var agentTable = new Table { Border = TableBorder.Rounded, Title = new TableTitle("By agent") };
        agentTable.AddColumn("Client");
        agentTable.AddColumn("Version");
        agentTable.AddColumn("Count");
        agentTable.AddColumn("Last seen");
        foreach (var agent in summary.Agents)
        {
            agentTable.AddRow(
                agent.Client.EscapeMarkup(),
                (agent.ClientVersion ?? "-").EscapeMarkup(),
                agent.Count.ToString(),
                $"{agent.LastSeen:yyyy-MM-dd HH:mm}");
        }

        console.Write(agentTable);

        var sessionTable = new Table { Border = TableBorder.Rounded, Title = new TableTitle("By session") };
        sessionTable.AddColumn("Session");
        sessionTable.AddColumn("Client");
        sessionTable.AddColumn("Count");
        sessionTable.AddColumn("Last seen");
        sessionTable.AddColumn("Top tools");
        foreach (var session in summary.Sessions)
        {
            sessionTable.AddRow(
                session.SessionId.EscapeMarkup(),
                session.Client.EscapeMarkup(),
                session.Count.ToString(),
                $"{session.LastSeen:yyyy-MM-dd HH:mm}",
                string.Join(", ", session.TopTools).EscapeMarkup());
        }

        console.Write(sessionTable);

        console.MarkupLine(
            $"Period: {summary.From:yyyy-MM-dd HH:mm} — {summary.To:yyyy-MM-dd HH:mm} ({summary.Records} records)");

        if (summary.Records > RetentionHintThreshold)
        {
            console.MarkupLine(
                $"[grey]Telemetry holds {summary.Records} records since {summary.From:d} — " +
                "run 'squeezr stats --reset' to clear.[/]");
        }
    }
}
