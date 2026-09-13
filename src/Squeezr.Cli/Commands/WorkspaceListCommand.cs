using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorkspaceListCommand : Command<WorkspaceListCommand.Settings>
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
            var repos = new WorkspaceManager(Directory.GetCurrentDirectory()).ListRepos();

            if (settings.Json)
            {
                foreach (var repo in repos)
                {
                    JsonOutput.WriteLine(repo);
                }

                return 0;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Repo");
            table.AddColumn("Path");
            foreach (var repo in repos)
            {
                table.AddRow(repo.Name.EscapeMarkup(), repo.Path.EscapeMarkup());
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }
}
