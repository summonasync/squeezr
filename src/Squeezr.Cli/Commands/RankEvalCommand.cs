using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Cli.RankEval;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class RankEvalCommand : Command<RankEvalCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--set")]
        public string? Set { get; set; }

        [CommandOption("--workspace")]
        public string? Workspace { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.Set is null)
            {
                throw new InvalidDataException("--set is required: path to the golden queries JSON");
            }

            var workspaceRoot = settings.Workspace is null
                ? WorkspaceRootResolver.RequireWorkspaceRoot()
                : Path.GetFullPath(settings.Workspace);
            var set = GoldenQuery.Load(settings.Set);
            var outcome = new RankEvalRunner(workspaceRoot).RunAsync(set, cancellationToken).GetAwaiter().GetResult();
            var rows = outcome.Rows;

            foreach (var warning in outcome.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            var mrr = rows.Average(r => r.Mrr);
            var p5 = rows.Average(r => r.PrecisionAt5);
            var recall = rows.Average(r => r.RecallAt10);

            if (settings.Json)
            {
                foreach (var row in rows)
                {
                    JsonOutput.WriteLine(new EvalQueryLine(
                        "eval-query", row.Source.Query, row.Mrr, row.PrecisionAt5, row.RecallAt10, row.Ranks));
                }

                JsonOutput.WriteLine(new EvalSummaryLine(
                    "eval-summary", rows.Count, mrr, p5, recall, outcome.SemanticDegraded));
                return 0;
            }

            AnsiConsole.MarkupLine($"[bold]rank-eval[/] {rows.Count} queries against {workspaceRoot.EscapeMarkup()}");
            foreach (var row in rows)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]mrr {row.Mrr:F3} p5 {row.PrecisionAt5:F3} r10 {row.RecallAt10:F3}[/] {row.Source.Query.EscapeMarkup()}");
                foreach (var (path, rank) in row.Ranks.Where(kv => kv.Value is 0 or > RankEvalScorer.RecallDepth))
                {
                    var verdict = rank == 0 ? "not ranked" : $"rank {rank}";
                    AnsiConsole.MarkupLine($"  [yellow]{verdict}[/]: {path.EscapeMarkup()}");
                }
            }

            AnsiConsole.MarkupLine($"[bold]aggregate[/] MRR@10 {mrr:F3}  P@5 {p5:F3}  Recall@10 {recall:F3}");
            if (outcome.SemanticDegraded)
            {
                AnsiConsole.MarkupLine("[grey]semantic arm degraded — metrics reflect exact-only ranking[/]");
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or InvalidDataException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }
}
