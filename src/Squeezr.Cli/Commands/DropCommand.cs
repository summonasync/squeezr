using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class DropCommand : Command<DropCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--store-key <KEY>")]
        public string? StoreKey { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            return settings.StoreKey is { } storeKey
                ? DropByStoreKey(storeKey, settings.Json, cancellationToken)
                : DropCurrentWorkspace(settings.Json, cancellationToken);
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static int DropCurrentWorkspace(bool json, CancellationToken cancellationToken)
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

        DeleteCollectionBestEffort(vectorStore, cancellationToken);

        return Report(result, json);
    }

    private static int DropByStoreKey(string storeKey, bool json, CancellationToken cancellationToken)
    {
        var entry = WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.FindByStoreKey(storeKey));
        if (entry is null)
        {
            throw new WorkspaceException($"No workspace is registered with store key '{storeKey}'.");
        }

        IVectorStore? vectorStore = null;
        try
        {
            var config = ConfigFile.Load(entry.RootPath);
            vectorStore = new QdrantVectorStore(config.Qdrant, entry.RootPath);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException)
        {
            Console.Error.WriteLine($"warning: Qdrant config unavailable for '{entry.RootPath}': {ex.Message}");
        }

        var removed = WorkspaceRootResolver.GuardRegistryIo(
            () => WorkspaceStore.DropByStoreKey(storeKey, StaleLockWarningSink.ToStderr));
        DeleteCollectionBestEffort(vectorStore, cancellationToken);

        return Report(new DropResult([.. removed], "squeezr services down"), json);
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

    private static void DeleteCollectionBestEffort(IVectorStore? vectorStore, CancellationToken cancellationToken)
    {
        if (vectorStore is null)
        {
            return;
        }

        try
        {
            var available = vectorStore.IsAvailableAsync(cancellationToken).GetAwaiter().GetResult();
            if (!available)
            {
                Console.Error.WriteLine(
                    "warning: Qdrant is not available; the vector collection may still exist in the shared volume.");
                return;
            }

            vectorStore.DeleteCollectionAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: failed to delete the Qdrant collection: {ex.Message}");
        }
    }
}
