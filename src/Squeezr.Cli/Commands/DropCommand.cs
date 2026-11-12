using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class DropCommand : AsyncCommand<DropCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--store-key <KEY>")]
        public string? StoreKey { get; set; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return settings.StoreKey is { } storeKey
                ? await DropByStoreKeyAsync(storeKey, settings.Json, cancellationToken)
                : await DropCurrentWorkspaceAsync(settings.Json, cancellationToken);
        }
        // Two removal verdicts and one unclassified permission failure: the permission arm exists because
        // an unhandled exception lands on stdout with exit 255 and breaks the --json contract, and because
        // a denial on the removal path is a workspace-store failure, not an internal one.
        catch (Exception ex)
            when (ex is WorkspaceException
                or WorkspaceStoreRemovalException
                or WorkspaceStoreRemovalUnverifiedException
                or UnauthorizedAccessException)
        {
            var reported = ex is UnauthorizedAccessException
                ? new WorkspaceException(ex.Message, WorkspaceException.DefaultCode, ex)
                : ex;
            return CommandError.Report(reported, settings.Json);
        }
    }

    private static async Task<int> DropCurrentWorkspaceAsync(bool json, CancellationToken cancellationToken)
    {
        var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
        var warnings = EmbeddingRuntimeFactory.TryCreate(
            workspaceRoot, withProvider: false, out _, out var vectorStore);
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        var manager = new WorkspaceManager(workspaceRoot);
        var result = WorkspaceRootResolver.GuardRegistryIo(() => manager.Drop(StaleLockWarningSink.ToStderr));

        if (vectorStore is not null)
        {
            await WorkspaceStoreDropper.RemoveCollectionAsync(vectorStore, cancellationToken);
        }

        return Report(result, json);
    }

    private static async Task<int> DropByStoreKeyAsync(
        string storeKey,
        bool json,
        CancellationToken cancellationToken)
    {
        var result = await WorkspaceStoreDropper.DropByStoreKeyAsync(storeKey, cancellationToken);
        return Report(new DropResult([.. result.RemovedPaths], "squeezr services down"), json);
    }

    private static int Report(DropResult result, bool json)
    {
        if (json)
        {
            JsonOutput.Write(result);
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]Removed[/] {string.Join(", ", result.Removed.Select(path => path.EscapeMarkup()))}");
        AnsiConsole.MarkupLine("To stop the shared Qdrant/Ollama services, run:");
        AnsiConsole.MarkupLine($"  [bold]{result.Hint}[/]");
        return 0;
    }
}
