using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorkspaceListCommand : Command<WorkspaceListCommand.Settings>
{
    private const string StaleDropHint =
        "run 'squeezr drop --store-key <key>' to remove this entry and its store";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkspaceListEntry> workspaces;
        try
        {
            workspaces = WorkspaceRootResolver.GuardRegistryIo(WorkspaceListing.Load);
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }

        if (settings.Json)
        {
            foreach (var workspace in workspaces)
            {
                JsonOutput.WriteLine(workspace);
            }

            return 0;
        }

        if (workspaces.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No workspaces registered. Run 'squeezr init' to create one.[/]");
            return 0;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Name");
        table.AddColumn("Root path");
        table.AddColumn("Store key");
        table.AddColumn("Last used");
        table.AddColumn("Status");
        foreach (var workspace in workspaces)
        {
            table.AddRow(
                workspace.Name.EscapeMarkup(),
                workspace.RootPath.EscapeMarkup(),
                workspace.StoreKey.EscapeMarkup(),
                workspace.LastUsedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                workspace.Stale ? "[yellow]stale[/]" : "[green]ok[/]");
        }

        AnsiConsole.Write(table);

        foreach (var stale in workspaces.Where(workspace => workspace.Stale))
        {
            Console.Error.WriteLine(
                $"warning: workspace '{stale.Name}' is stale: root path '{stale.RootPath}' does not exist; {StaleDropHint}.");
        }

        return 0;
    }
}
