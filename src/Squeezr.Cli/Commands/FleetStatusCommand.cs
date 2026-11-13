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
    private const string DesiredUnknown = "unknown";

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
                DesiredCell(entry.Desired),
                Colorize(entry.Supervision),
                FormatLastPass(entry.LastPass));
        }

        AnsiConsole.Write(table);
        if (report.StateFault is { } fault)
        {
            Console.Error.WriteLine(
                $"warning: watchd state '{fault}' — no desired state is known, the supervisor applies nothing " +
                "until the file is repaired; the supervision column shows live facts only.");
        }

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
        var supervision = new SupervisionState().Read();
        var staleKeys = OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .ToHashSet(StringComparer.Ordinal);
        var entries = WorkspaceRegistry.Load();
        return new FleetStatusReport(
            entries.Select(entry =>
                BuildEntry(entry, now, watchd, supervision, staleKeys.Contains(entry.StoreKey))).ToList(),
            watchd,
            supervision.Status == SupervisionStateStatus.Unreadable
                ? $"{supervision.FilePath} {supervision.Fault}"
                : null);
    }

    private static FleetStatusEntry BuildEntry(
        WorkspaceRegistryEntry entry,
        DateTime now,
        FleetWatchdState watchd,
        SupervisionSnapshot supervision,
        bool stale)
    {
        var storeDir = SqueezrPaths.StoreDir(entry);
        var health = WatchStoreHealth.Probe(storeDir, now);
        // An unreadable file answers "unknown" for the wish and leaves the posture to the body: the live
        // facts behind supervision do not depend on the desire unless it says stopped, and a corrupt file
        // cannot say that. Guessing a wish is what R3 forbids; guessing nothing is not.
        var desire = supervision.DesireOf(entry.StoreKey);
        return new FleetStatusEntry(
            entry.Name,
            entry.StoreKey,
            supervision.Status == SupervisionStateStatus.Unreadable
                ? DesiredUnknown
                : desire.ToString().ToLowerInvariant(),
            FleetSupervisionPosture.Resolve(desire, health, watchd.Running),
            health.State.ToString().ToLowerInvariant(),
            health.OwnerPid,
            health.OwnerKind,
            PassPhaseStampFile.TryRead(storeDir),
            stale);
    }

    private static string DesiredCell(string desired) => desired switch
    {
        "running" => "[green]running[/]",
        "stopped" => "[grey]stopped[/]",
        _ => $"[red]{desired.EscapeMarkup()}[/]",
    };

    private static string Colorize(string supervision) => supervision switch
    {
        FleetSupervisionPosture.Supervised => "[green]supervised[/]",
        FleetSupervisionPosture.Wedged => "[red]wedged[/]",
        FleetSupervisionPosture.ManagedElsewhere => "[yellow]managed elsewhere[/]",
        FleetSupervisionPosture.Starting => "[yellow]starting[/]",
        FleetSupervisionPosture.Idle => "[yellow]idle (no supervisor)[/]",
        _ => "[grey]stopped[/]",
    };

    private static string FormatLastPass(PassPhaseStamp? stamp) => stamp is null
        ? "—"
        : $"{stamp.Phase.EscapeMarkup()} · {stamp.LastProgressAt.ToLocalTime():yyyy-MM-dd HH:mm}";
}
