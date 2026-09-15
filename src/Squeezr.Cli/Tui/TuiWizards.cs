using Spectre.Console;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiWizards
{
    public static async Task InitAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        var defaultName = new DirectoryInfo(workspaceRoot).Name;
        var name = await new TextPrompt<string>("Workspace name:")
            .DefaultValue(defaultName)
            .ShowAsync(console, cancellationToken);

        try
        {
            var result = new WorkspaceManager(workspaceRoot).Init(string.IsNullOrWhiteSpace(name) ? null : name.Trim());
            console.MarkupLine($"[green]Initialized workspace[/] {result.WorkspaceName.EscapeMarkup()} in {result.RootPath.EscapeMarkup()}");
            console.MarkupLine("Next: add a repository from the Wizards menu.");
        }
        catch (WorkspaceException ex)
        {
            console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
        }
    }

    public static async Task AddAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        var path = await new TextPrompt<string>("Repository path:")
            .Validate(p => Directory.Exists(ResolvePath(workspaceRoot, p))
                ? ValidationResult.Success()
                : ValidationResult.Error($"Path '{ResolvePath(workspaceRoot, p)}' does not exist or is not a directory."))
            .ShowAsync(console, cancellationToken);

        var fullPath = ResolvePath(workspaceRoot, path);
        var defaultName = new DirectoryInfo(fullPath).Name;
        var name = await new TextPrompt<string>("Repository name:")
            .DefaultValue(defaultName)
            .ShowAsync(console, cancellationToken);

        try
        {
            var entry = new WorkspaceManager(workspaceRoot)
                .AddRepo(
                    fullPath,
                    string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    StaleLockWarningSink.ToStderr);
            console.MarkupLine($"[green]Added repo[/] {entry.Name.EscapeMarkup()} at {entry.Path.EscapeMarkup()}");
        }
        catch (WorkspaceException ex)
        {
            console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
        }
    }

    public static async Task DropAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        var storeDir = SqueezrPaths.StoreDir(workspaceRoot);
        if (!Directory.Exists(storeDir))
        {
            console.MarkupLine("[red]error:[/] Nothing to drop: no '.squeezr' store in this directory.");
            return;
        }

        console.MarkupLine("The following will be removed:");
        console.MarkupLine($"  [red]-[/] {SqueezrPaths.StoreDirName}{Path.DirectorySeparatorChar} ({storeDir.EscapeMarkup()})");
        if (File.Exists(SqueezrPaths.ManifestPath(workspaceRoot)))
        {
            console.MarkupLine($"  [red]-[/] {SqueezrPaths.ManifestFileName}");
        }

        if (File.Exists(SqueezrPaths.ComposePath(workspaceRoot)))
        {
            console.MarkupLine($"  [red]-[/] {SqueezrPaths.ComposeFileName}");
        }

        var collection = QdrantVectorStore.GetCollectionName(workspaceRoot);
        console.MarkupLine($"Qdrant collection [bold]{collection}[/] will be deleted if Qdrant is available.");

        var confirmed = await new ConfirmationPrompt("Drop this workspace?")
            {
                DefaultValue = false
            }
            .ShowAsync(console, cancellationToken);
        if (!confirmed)
        {
            console.MarkupLine("[grey]Aborted. Nothing was removed.[/]");
            return;
        }

        var warnings = EmbeddingRuntimeFactory.TryCreate(workspaceRoot, withProvider: false, out _, out var vectorStore);
        foreach (var warning in warnings)
        {
            console.MarkupLine($"[grey]{warning.EscapeMarkup()}[/]");
        }

        try
        {
            var result = new WorkspaceManager(workspaceRoot).Drop(StaleLockWarningSink.ToStderr);
            await DeleteCollectionBestEffort(console, vectorStore, cancellationToken);
            console.MarkupLine($"[green]Removed[/] {string.Join(", ", result.Removed)}");
            console.MarkupLine("To remove the Qdrant data volume as well, run:");
            console.MarkupLine($"  [bold]{result.Hint}[/]");
        }
        catch (WorkspaceException ex)
        {
            console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
        }
    }

    private static string ResolvePath(string workspaceRoot, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));

    private static async Task DeleteCollectionBestEffort(IAnsiConsole console, IVectorStore? vectorStore, CancellationToken cancellationToken)
    {
        if (vectorStore is null)
        {
            return;
        }

        try
        {
            var available = await vectorStore.IsAvailableAsync(cancellationToken);
            if (!available)
            {
                console.MarkupLine(
                    "[grey]Qdrant is not available; the vector collection may still exist. " +
                    "Run 'docker compose down -v' to remove its volume.[/]");
                return;
            }

            await vectorStore.DeleteCollectionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[grey]Failed to delete the Qdrant collection: {ex.Message.EscapeMarkup()}[/]");
        }
    }
}
