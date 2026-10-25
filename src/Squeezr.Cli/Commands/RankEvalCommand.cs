using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Cli.RankEval;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Serialization;
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

        [CommandOption("--sweep")]
        public bool Sweep { get; set; }

        [CommandOption("--out")]
        public string? Out { get; set; }

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
            var runner = new RankEvalRunner(workspaceRoot);

            if (settings.Sweep)
            {
                return RunSweep(runner, set, settings, cancellationToken);
            }

            var outcome = runner.RunAsync(set, cancellationToken).GetAwaiter().GetResult();
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

    private static int RunSweep(
        RankEvalRunner runner, IReadOnlyList<GoldenQuery> set, Settings settings, CancellationToken cancellationToken)
    {
        var rows = runner.RunSweepAsync(set, cancellationToken).GetAwaiter().GetResult();
        var baseline = rows.FirstOrDefault(r => r.K == RrfMerger.K);

        if (settings.Json)
        {
            foreach (var row in rows)
            {
                JsonOutput.WriteLine(new EvalSweepLine(
                    "eval-sweep", row.K, row.Mrr, row.P5, row.Recall, row.SemanticDegraded));
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold]rank-eval sweep[/] K grid: {string.Join(", ", rows.Select(r => r.K))}");
            foreach (var row in rows)
            {
                var marker = row.K == RrfMerger.K ? " [bold](baseline)[/]" : string.Empty;
                AnsiConsole.MarkupLine(
                    $"K {row.K,4}  MRR@10 {row.Mrr:F3}  P@5 {row.P5:F3}  Recall@10 {row.Recall:F3}{marker}");
            }
        }

        if (baseline is not null && !settings.Json)
        {
            foreach (var row in rows.Where(r => r.K != RrfMerger.K))
            {
                var dmrr = row.Mrr - baseline.Mrr;
                var dp5 = row.P5 - baseline.P5;
                var dr10 = row.Recall - baseline.Recall;
                AnsiConsole.MarkupLine(
                    $"[grey]K {row.K,4} vs baseline: dMRR {dmrr:+0.000;-0.000;0.000}  dP5 {dp5:+0.000;-0.000;0.000}  dR10 {dr10:+0.000;-0.000;0.000}[/]");
            }
        }

        if (settings.Out is not null)
        {
            File.WriteAllText(
                settings.Out,
                JsonSerializer.Serialize(new EvalSweepOut("rank-sweep", rows), SqueezrJson.OutputOptions));
            if (!settings.Json)
            {
                AnsiConsole.MarkupLine($"[grey]sweep report written to {settings.Out.EscapeMarkup()}[/]");
            }
        }

        return 0;
    }
}
