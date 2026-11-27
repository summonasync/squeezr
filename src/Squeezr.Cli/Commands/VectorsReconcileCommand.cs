using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Semantic.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Indexing;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Cli.Commands;

public sealed class VectorsReconcileCommand : AsyncCommand<VectorsReconcileCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Report the drift and delete nothing — the default when no flag is given")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description(
            "Delete orphan points and reset stale embedded markers in meta.db; without this flag nothing is written")]
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

            if (settings.Apply)
            {
                await TryDropLegacyCollectionsAsync(workspaceRoot, vectorStore, settings, cancellationToken);
            }

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

    // the 078 auto-drop: owned workspace just reconciled into the fresh collection — the squeezr-named
    // collections on the standard-port endpoint (where the pre-owned instance lived) are now redundant.
    // Health precondition first: the owned endpoint must answer, or the legacy data is not redundant yet.
    private static async Task TryDropLegacyCollectionsAsync(
        string workspaceRoot, IVectorStore? vectorStore, Settings settings, CancellationToken cancellationToken)
    {
        if (!SemanticConfigFile.Load(workspaceRoot).Qdrant.Owned)
        {
            return;
        }

        if (vectorStore is null || !await vectorStore.IsAvailableAsync(cancellationToken))
        {
            return;
        }

        var allowed = WorkspaceRegistry.Load().Select(entry => entry.StoreKey).ToList();
        if (allowed.Count == 0)
        {
            return;
        }

        var reap = await LegacyCollectionReaper.DropAsync("http://localhost:6333", allowed, cancellationToken);
        if (reap.Dropped.Count > 0)
        {
            var line = $"Auto-dropped {reap.Dropped.Count} legacy collection(s): {string.Join(", ", reap.Dropped)}";
            if (settings.Json)
            {
                Console.Error.WriteLine(line);
            }
            else
            {
                AnsiConsole.MarkupLine($"[grey]{line.EscapeMarkup()}[/]");
            }
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

        // StaleMarkers is a subset of ChunksWithoutPoints: those rows claim a vector that is gone, so naming
        // them "pending embedding" would tell the reader the work is already queued when it is not.
        if (NeverEmbeddedLine(report) is { } pendingLine)
        {
            AnsiConsole.MarkupLine($"[grey]{pendingLine.EscapeMarkup()}[/]");
        }

        if (report.StaleMarkers > 0)
        {
            if (report.Applied)
            {
                AnsiConsole.MarkupLine(
                    $"[green]Re-queued {report.Requeued} chunk(s) whose vectors are gone — run 'squeezr index' " +
                    "with embeddings available to re-embed them (watch passes do not embed).[/]");
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

    internal static string? NeverEmbeddedLine(VectorReconcileReport report)
    {
        var neverEmbedded = report.ChunksWithoutPoints - report.StaleMarkers;
        return neverEmbedded > 0
            ? $"{neverEmbedded} chunk(s) have no point yet — never embedded."
            : null;
    }
}
