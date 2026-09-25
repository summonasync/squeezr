using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorkspaceRepairCommand : Command<WorkspaceRepairCommand.Settings>
{
    private const string HuskDropHint =
        "run 'squeezr drop --store-key <key>' to remove this entry and its store";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--store-key <STORE_KEY>")]
        public string? StoreKey { get; set; }

        [CommandOption("--root <PATH>")]
        public string? Root { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if ((settings.StoreKey is null) != (settings.Root is null))
            {
                throw new McpUsageException("--store-key and --root must be provided together.");
            }

            if (settings.StoreKey is not null)
            {
                var entry = WorkspaceRootResolver.GuardRegistryIo(
                    () => RegistryRepair.AdoptWithRoot(settings.StoreKey, settings.Root!));
                var adopted = new WorkspaceRepairAdoptedEntry(entry.Name, entry.StoreKey, entry.RootPath);
                if (settings.Json)
                {
                    JsonOutput.Write(adopted);
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"Adopted workspace [bold]{entry.Name.EscapeMarkup()}[/] " +
                        $"({entry.StoreKey.EscapeMarkup()}) with root {entry.RootPath.EscapeMarkup()}");
                }

                return 0;
            }

            var outcome = WorkspaceRootResolver.GuardRegistryIo(RegistryRepair.AdoptAll);
            var report = new WorkspaceRepairReport(
                outcome.Adopted.Select(orphan => new WorkspaceRepairAdoptedEntry(orphan.Name, orphan.StoreKey, orphan.RootPath!)).ToList(),
                outcome.NeedsRoot.Select(orphan => new WorkspaceRepairNeedsRootEntry(orphan.Name, orphan.StoreKey)).ToList(),
                outcome.Husks.Select(husk => new WorkspaceRepairHuskEntry(husk.Name, husk.StoreKey, husk.RootPath)).ToList());

            WriteHuskHints(report.Husks);

            if (settings.Json)
            {
                JsonOutput.Write(report);
            }
            else
            {
                RenderReport(report);
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void RenderReport(WorkspaceRepairReport report)
    {
        if (report.Adopted.Count == 0 && report.NeedsRoot.Count == 0 && report.Husks.Count == 0)
        {
            AnsiConsole.MarkupLine("Nothing to repair: no orphaned workspace stores found.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"Repair report: [green]{report.Adopted.Count} adopted[/], " +
            $"[yellow]{report.NeedsRoot.Count} need --root[/], " +
            $"{report.Husks.Count} husk(s) left untouched");
        foreach (var adopted in report.Adopted)
        {
            AnsiConsole.MarkupLine(
                $"  [green]+[/] adopted {adopted.Name.EscapeMarkup()} " +
                $"({adopted.StoreKey.EscapeMarkup()}) → {adopted.RootPath.EscapeMarkup()}");
        }

        foreach (var orphan in report.NeedsRoot)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]![/] needs root: {orphan.Name.EscapeMarkup()} ({orphan.StoreKey.EscapeMarkup()}) — " +
                $"re-run with --store-key {orphan.StoreKey.EscapeMarkup()} --root <path>");
        }
    }

    private static void WriteHuskHints(IReadOnlyList<WorkspaceRepairHuskEntry> husks)
    {
        foreach (var husk in husks)
        {
            Console.Error.WriteLine(
                $"warning: workspace '{husk.Name}' looks like a husk (root path '{husk.RootPath}'); {HuskDropHint}.");
        }
    }
}
