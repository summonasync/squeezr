using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
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
            var manager = new WorkspaceManager(Directory.GetCurrentDirectory());
            var result = manager.Drop();

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
}
