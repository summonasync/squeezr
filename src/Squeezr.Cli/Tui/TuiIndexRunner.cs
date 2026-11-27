using Spectre.Console;
using Squeezr.Semantic.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Indexing.Watch;

namespace Squeezr.Cli.Tui;

public static class TuiIndexRunner
{
    private static readonly string[] ModeItems = ["Incremental (default)", "Full reindex", "Reembed vectors", "Back"];

    public static async Task RunAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            console.MarkupLine("[yellow]Workspace not initialized.[/] Run 'squeezr init' first.");
            return;
        }

        var mode = await new SelectionPrompt<string>()
            .Title("Index mode")
            .AddChoices(ModeItems)
            .ShowAsync(console, cancellationToken);
        if (mode == "Back")
        {
            return;
        }

        var full = mode == "Full reindex";
        var reembed = mode == "Reembed vectors";
        var noSemantic = IndexingMode.EffectiveNoSemantic(workspaceRoot);

        var warnings = EmbeddingRuntimeFactory.TryCreate(
            workspaceRoot, withProvider: reembed || !noSemantic,
            out var embeddings, out var vectorStore, out var providerResolution);
        var providerLine = EmbeddingProviderLogLine.Describe(embeddings is null ? null : providerResolution);
        if (providerLine is not null)
        {
            console.MarkupLine($"[grey]{providerLine.EscapeMarkup()}[/]");
        }

        foreach (var warning in warnings)
        {
            console.MarkupLine($"[grey]{warning.EscapeMarkup()}[/]");
        }

        var embeddingsUnavailable = embeddings is null && !noSemantic
            ? warnings.Count > 0
                ? $"provider unavailable: {string.Join("; ", warnings)}"
                : "provider unavailable"
            : null;
        var vectorPhase = embeddings is not null && vectorStore is not null
            ? new WatchEmbeddingRuntime(embeddings, vectorStore, providerResolution)
            : null;

        var indexer = new WorkspaceIndexer();
        IndexResult result;
        try
        {
            result = console.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .Start(ctx => indexer.Index(
                    workspaceRoot, null, full, false,
                    new IndexProgressRenderer(ctx), vectorPhase, reembed,
                    StaleLockWarningSink.ToStderr, embeddingsUnavailable));
        }
        catch (WorkspaceException ex)
        {
            console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
            return;
        }

        foreach (var repo in result.Repos)
        {
            console.MarkupLine(
                $"[bold]{repo.Name.EscapeMarkup()}[/]: +{repo.Added} ~{repo.Modified} -{repo.Deleted} " +
                $"(skipped: {repo.Skipped}, files: {repo.Files})");
        }

        foreach (var warning in result.Warnings)
        {
            console.MarkupLine($"[yellow]warning:[/] {warning.EscapeMarkup()}");
        }
    }
}
