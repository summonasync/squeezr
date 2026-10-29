using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetStatusCommand : Command<FleetStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        FleetStatusReport report;
        try
        {
            SqueezrHome.RequireHabitableProfileHome();
            report = WorkspaceRootResolver.GuardRegistryIo(LoadReport);
        }
        catch (Exception ex) when (ex is WorkspaceException or ArgumentException)
        {
            return CommandError.Report(ex, settings.Json);
        }

        if (settings.Json)
        {
            JsonOutput.WriteLine(report);
            return 0;
        }

        if (report.Stores.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No workspaces registered. Run 'squeezr init' to create one.[/]");
            return 0;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Workspace");
        table.AddColumn("Store key");
        table.AddColumn("Desired");
        table.AddColumn("Supervision");
        table.AddColumn("Last pass");
        foreach (var entry in report.Stores)
        {
            table.AddRow(
                entry.Stale ? $"{entry.Name.EscapeMarkup()} [yellow](stale)[/]" : entry.Name.EscapeMarkup(),
                entry.StoreKey.EscapeMarkup(),
                entry.Desired == WatchDesire.Running.ToString().ToLowerInvariant()
                    ? "[green]running[/]"
                    : "[grey]stopped[/]",
                Colorize(entry.Supervision),
                FormatLastPass(entry.LastPass));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(report.Watchd.Running
            ? $"[grey]watchd[/]: running (pid {report.Watchd.Pid})"
            : "[grey]watchd[/]: [yellow]not running[/]");
        if (!report.Watchd.Running)
        {
            Console.Error.WriteLine(
                "warning: watchd is not running — desired states are recorded but nothing applies them; " +
                "start it with 'squeezr watchd'.");
        }

        return 0;
    }

    internal static FleetStatusReport LoadReport()
    {
        var watchd = FleetWatchdProbe.Probe();
        var now = DateTime.UtcNow;
        var staleKeys = OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .ToHashSet(StringComparer.Ordinal);
        var entries = WorkspaceRegistry.Load();
        return new FleetStatusReport(
            entries.Select(entry => BuildEntry(entry, now, watchd, staleKeys.Contains(entry.StoreKey))).ToList(),
            watchd);
    }

    private static FleetStatusEntry BuildEntry(
        WorkspaceRegistryEntry entry,
        DateTime now,
        FleetWatchdState watchd,
        bool stale)
    {
        var storeDir = SqueezrPaths.StoreDir(entry.RootPath);
        var health = WatchStoreHealth.Probe(storeDir, now);
        var desired = new SupervisionState().DesireOf(entry.StoreKey);
        return new FleetStatusEntry(
            entry.Name,
            entry.StoreKey,
            desired.ToString().ToLowerInvariant(),
            FleetSupervisionPosture.Resolve(desired, health, watchd.Running),
            health.State.ToString().ToLowerInvariant(),
            health.OwnerPid,
            health.OwnerKind,
            PassPhaseStampFile.TryRead(storeDir),
            stale);
    }

    private static string Colorize(string supervision) => supervision switch
    {
        FleetSupervisionPosture.Supervised => "[green]supervised[/]",
        FleetSupervisionPosture.Wedged => "[red]wedged[/]",
        FleetSupervisionPosture.ManagedElsewhere => "[yellow]managed elsewhere[/]",
        FleetSupervisionPosture.Starting => "[yellow]starting[/]",
        _ => "[grey]stopped[/]",
    };

    private static string FormatLastPass(PassPhaseStamp? stamp) => stamp is null
        ? "—"
        : $"{stamp.Phase.EscapeMarkup()} · {stamp.LastProgressAt.ToLocalTime():yyyy-MM-dd HH:mm}";
}
