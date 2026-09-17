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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
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

            Render(summary);
            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static int Reset(string workspaceRoot, bool json)
    {
        var removed = QueryTelemetry.Count(workspaceRoot);
        var path = SqueezrPaths.QueriesLogPath(workspaceRoot);
        if (File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

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

    private static void Render(QueryStatsSummary summary)
    {
        if (summary.Records == 0)
        {
            AnsiConsole.MarkupLine("No query telemetry yet.");
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

        AnsiConsole.Write(table);

        var savings = summary.Savings;
        var ratio = savings.Ratio.ToString("F1", CultureInfo.InvariantCulture);
        AnsiConsole.MarkupLine(
            $"Savings: {savings.ActualTokens} actual vs {savings.CounterfactualTokens} counterfactual tokens — " +
            $"[green]saved {savings.SavedTokens}[/] (×{ratio})");
        if (savings.UnknownFiles > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{savings.UnknownFiles} expanded file(s) not in the index, excluded from counterfactual.[/]");
        }

        var hitRate = summary.Cache.HitRate.ToString("P1", CultureInfo.InvariantCulture);
        AnsiConsole.MarkupLine(
            $"Cache hit-rate: {hitRate} across {summary.Cache.SessionsWithCache} session(s)");

        AnsiConsole.MarkupLine(
            $"Period: {summary.From:yyyy-MM-dd HH:mm} — {summary.To:yyyy-MM-dd HH:mm} ({summary.Records} records)");

        if (summary.Records > RetentionHintThreshold)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Telemetry holds {summary.Records} records since {summary.From:d} — " +
                "run 'squeezr stats --reset' to clear.[/]");
        }
    }
}
