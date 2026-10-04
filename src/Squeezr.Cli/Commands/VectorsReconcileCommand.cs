using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class VectorsReconcileCommand : AsyncCommand<VectorsReconcileCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Report the drift and delete nothing — the default when no flag is given")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description("Delete the orphan points; without this flag the command never writes to Qdrant")]
        [CommandOption("--apply")]
        public bool Apply { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.DryRun && settings.Apply)
            {
                throw new McpUsageException("Options --dry-run and --apply are mutually exclusive.");
            }

            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: false, out _, out var vectorStore);
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            if (vectorStore is null)
            {
                throw new WorkspaceException(
                    "This workspace has no vector store configuration — there is no collection to reconcile.");
            }

            var report = await VectorPointReconciler.ReconcileAsync(
                workspaceRoot, vectorStore, settings.Apply, StaleLockWarningSink.ToStderr, cancellationToken);

            if (settings.Json)
            {
                JsonOutput.Write(report);
                return 0;
            }

            Render(QdrantVectorStore.GetCollectionName(workspaceRoot), report);
            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void Render(string collection, VectorReconcileReport report)
    {
        var points = report.PointsAfter is { } after
            ? $"{report.PointsBefore} → [bold]{after}[/]"
            : $"[bold]{report.PointsBefore}[/]";
        AnsiConsole.MarkupLine(
            $"Collection {collection.EscapeMarkup()}: {points} point(s) · {report.Chunks} chunk(s) · " +
            $"{report.LiveHashes} distinct chunk hash(es)");

        if (report.Orphans == 0)
        {
            AnsiConsole.MarkupLine("[green]No orphan points — the collection matches the index.[/]");
        }
        else if (report.Applied)
        {
            AnsiConsole.MarkupLine($"[green]Deleted {report.Orphans} orphan point(s).[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{report.Orphans} orphan point(s)[/] — ids no live chunk hash maps to " +
                $"(sample: {string.Join(", ", report.OrphanSample)})");
            AnsiConsole.MarkupLine(
                "[grey]Dry run: nothing was deleted. Re-run with --apply to remove them.[/]");
        }

        if (report.ChunksWithoutPoints > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{report.ChunksWithoutPoints} chunk(s) have no point yet — pending embedding.[/]");
        }

        if (report.StaleMarkers > 0)
        {
            if (report.Applied)
            {
                AnsiConsole.MarkupLine(
                    $"[green]Re-queued {report.Requeued} chunk(s) whose vectors are gone — run 'squeezr index' to embed them.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{report.StaleMarkers} chunk(s) are marked embedded but have no point in the collection[/]");
                AnsiConsole.MarkupLine(
                    "[grey]Dry run: nothing was re-queued. Re-run with --apply to put them back in the embedding queue.[/]");
            }
        }
    }
}
