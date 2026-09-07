using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class StatusCommand : Command<StatusCommand.Settings>
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
            var manager = new WorkspaceManager(workspaceRoot);
            var status = manager.GetStatus();

            IVectorStore? vectorStore = null;
            if (File.Exists(SqueezrPaths.ConfigPath(workspaceRoot)))
            {
                try
                {
                    var config = ConfigFile.Load(workspaceRoot);
                    vectorStore = new QdrantVectorStore(config.Qdrant, workspaceRoot);
                }
                catch (WorkspaceException ex)
                {
                    Console.Error.WriteLine($"warning: {ex.Message}");
                }
            }

            QdrantStatus qdrant;
            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
            {
                qdrant = QdrantStatusProbe.ProbeAsync(vectorStore, store, cancellationToken).GetAwaiter().GetResult();
            }

            status = status with { Qdrant = qdrant };

            if (settings.Json)
            {
                JsonOutput.Write(status);
                return 0;
            }

            AnsiConsole.MarkupLine(
                $"Workspace [bold]{status.Name.EscapeMarkup()}[/] — {status.Repos.Count} repo(s), " +
                $"{status.Repos.Sum(r => r.Files)} file(s) indexed");
            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Repo");
            table.AddColumn("Path");
            table.AddColumn("Files");
            table.AddColumn("Symbols");
            table.AddColumn("Chunks");
            table.AddColumn("Last indexed");

            foreach (var repo in status.Repos)
            {
                table.AddRow(
                    repo.Name.EscapeMarkup(),
                    repo.Path.EscapeMarkup(),
                    repo.Files.ToString(),
                    repo.Symbols.ToString(),
                    repo.Chunks.ToString(),
                    repo.LastIndexedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-");
            }

            AnsiConsole.Write(table);

            if (qdrant.Available)
            {
                AnsiConsole.MarkupLine(
                    $"Qdrant: [green]available[/] — {qdrant.Points} point(s), {qdrant.PendingChunks} pending chunk(s)");
            }
            else
            {
                AnsiConsole.MarkupLine($"Qdrant: [red]unavailable[/] — {qdrant.PendingChunks} pending chunk(s)");
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
