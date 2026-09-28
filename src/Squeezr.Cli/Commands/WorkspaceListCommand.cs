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
        WorkspaceListingResult listing;
        try
        {
            listing = WorkspaceRootResolver.GuardRegistryIo(WorkspaceListing.Load);
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }

        if (settings.Json)
        {
            foreach (var workspace in listing.Workspaces)
            {
                JsonOutput.WriteLine(workspace);
            }

            foreach (var orphan in listing.Orphans)
            {
                JsonOutput.WriteLine(orphan);
            }

            WriteHints(listing);
            return 0;
        }

        if (listing.Workspaces.Count == 0 && listing.Orphans.Count == 0)
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
        foreach (var workspace in listing.Workspaces)
        {
            var status = workspace.Husk == true
                ? "[yellow]husk[/]"
                : workspace.Stale ? "[yellow]stale[/]" : "[green]ok[/]";
            table.AddRow(
                workspace.Name.EscapeMarkup(),
                workspace.RootPath.EscapeMarkup(),
                workspace.StoreKey.EscapeMarkup(),
                workspace.LastUsedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                status);
        }

        foreach (var orphan in listing.Orphans)
        {
            table.AddRow(
                $"{orphan.Name.EscapeMarkup()} [grey](orphan)[/]",
                orphan.RootPath?.EscapeMarkup() ?? "[yellow]unknown — needs --root[/]",
                orphan.StoreKey.EscapeMarkup(),
                "-",
                orphan.Class == "legacy" ? "[yellow]orphan — needs root[/]" : "[yellow]orphan — adoptable[/]");
        }

        AnsiConsole.Write(table);

        foreach (var stale in listing.Workspaces.Where(workspace => workspace.Stale))
        {
            Console.Error.WriteLine(
                $"warning: workspace '{stale.Name}' is stale: root path '{stale.RootPath}' does not exist; {StaleDropHint}.");
        }

        WriteHints(listing);
        return 0;
    }

    private static void WriteHints(WorkspaceListingResult listing)
    {
        foreach (var orphan in listing.Orphans)
        {
            Console.Error.WriteLine(orphan.Class == "legacy"
                ? $"warning: workspace store '{orphan.StoreKey}' has no recorded root path; run 'squeezr workspace repair --store-key {orphan.StoreKey} --root <path>'."
                : $"warning: workspace store '{orphan.StoreKey}' is an orphan; run 'squeezr workspace repair' to adopt it.");
        }

        foreach (var workspace in listing.Workspaces.Where(entry => entry.Husk == true))
        {
            Console.Error.WriteLine(
                $"warning: workspace '{workspace.Name}' looks like a husk (root path '{workspace.RootPath}'); {StaleDropHint}.");
        }
    }
}
