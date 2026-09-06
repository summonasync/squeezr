using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
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
            var manager = new WorkspaceManager(Directory.GetCurrentDirectory());
            var status = manager.GetStatus();

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
            return 0;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
