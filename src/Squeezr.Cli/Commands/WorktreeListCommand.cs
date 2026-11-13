using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorktreeListCommand : Command<WorktreeListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Checkout to list the family of; defaults to the current directory")]
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var target = string.IsNullOrWhiteSpace(settings.Path)
            ? WorkspaceRootResolver.LaunchDirectory()
            : settings.Path!;

        WorktreeListReport report;
        try
        {
            // Read-only in both directions: the family is assembled from the registry and the presence of each
            // row's directory, and nothing here mints a row, a store or a lock. A gone path leaves this door as
            // the refusal the read verbs share, with the same fields `worktree resolve' answers with.
            SqueezrHome.RequireHabitableProfileHome();
            report = WorktreeListReport.From(
                WorkspaceRootResolver.GuardRegistryIo(() => WorktreeListing.List(target)));
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, settings.Json);
        }

        if (settings.Json)
        {
            JsonOutput.Write(report);
            foreach (var row in report.Rows.Where(entry => !entry.TreePresent))
            {
                Console.Error.WriteLine($"warning: {DeadRowText(row)}");
            }

            return 0;
        }

        WriteHuman(report);
        return 0;
    }

    private static void WriteHuman(WorktreeListReport report)
    {
        AnsiConsole.MarkupLine($"  family: {(report.FamilyRootPath ?? "none — nothing anchors this path").EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  queried: {report.QueriedRootPath?.EscapeMarkup() ?? "—"}");
        if (report.QueriedBranch is { } branch)
        {
            AnsiConsole.MarkupLine($"  queried branch: {branch.EscapeMarkup()}");
        }

        if (report.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine($"  [yellow]no rows[/] — {report.Reason!.EscapeMarkup()}");
            return;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Role");
        table.AddColumn("Name");
        table.AddColumn("Store key");
        table.AddColumn("Branch");
        table.AddColumn("Kind");
        table.AddColumn("Tree");
        foreach (var row in report.Rows)
        {
            table.AddRow(
                row.Role.ToString().ToLowerInvariant().EscapeMarkup()
                    + (row.IsCurrent ? " [cyan]*[/]" : string.Empty),
                row.Name.EscapeMarkup(),
                row.StoreKey.EscapeMarkup(),
                row.Branch?.EscapeMarkup() ?? "[grey]not recorded[/]",
                row.Kind switch
                {
                    WorkspaceKind.Worktree => "worktree",
                    WorkspaceKind.Nested => "nested",
                    null => "[grey]workspace[/]",
                    _ => row.Kind.ToString()!.ToLowerInvariant().EscapeMarkup(),
                },
                row.TreePresent ? "[green]present[/]" : "[yellow]gone[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"  [grey]* the row this path resolves to — {report.RowCount} row(s), {report.RowsOnDisk} " +
            "checkout(s) still on disk[/]");

        foreach (var row in report.Rows.Where(entry => !entry.TreePresent))
        {
            Console.Error.WriteLine($"warning: {DeadRowText(row)}");
        }
    }

    // A dead row is reported on the warning channel as well as in the table, because the table is what a human
    // reads and the warning is what a supervision pass reads — and a family list is usually opened precisely
    // because somebody suspects the litter.
    private static string DeadRowText(WorktreeFamilyRow row) =>
        $"registry row '{row.StoreKey}' names '{row.RootPath}' as its {row.Role.ToString().ToLowerInvariant()} " +
        "checkout, which is not on disk; its store stays where it is, and the row is reaped with " +
        $"{RemedyText.WorkspaceRepairCommand}";
}
