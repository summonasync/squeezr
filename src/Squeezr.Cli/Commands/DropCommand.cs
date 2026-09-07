using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: false, out _, out var vectorStore);
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            var manager = new WorkspaceManager(workspaceRoot);
            var result = manager.Drop();

            DeleteCollectionBestEffort(vectorStore, cancellationToken);

            if (settings.Json)
            {
                JsonOutput.Write(result);
                return 0;
            }

            AnsiConsole.MarkupLine($"[green]Removed[/] {SqueezrPaths.StoreDirName}");
            AnsiConsole.MarkupLine("To remove the Qdrant data volume as well, run:");
            AnsiConsole.MarkupLine($"  [bold]{result.Hint}[/]");
            return 0;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
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
                    "warning: Qdrant is not available; the vector collection may still exist. " +
                    "Run 'docker compose down -v' to remove its volume.");
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
