using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetCommand : Command<FleetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<FleetEntry> fleet;
        try
        {
            fleet = WorkspaceRootResolver.GuardRegistryIo(FleetListing.Load);
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }

        if (settings.Json)
        {
            foreach (var entry in fleet)
            {
                JsonOutput.WriteLine(entry);
            }

            return 0;
        }

        if (fleet.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No workspaces registered. Run 'squeezr init' to create one.[/]");
            return 0;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Name");
        table.AddColumn("Root path");
        table.AddColumn("Store key");
        table.AddColumn("WATCH");
        table.AddColumn("SERVE-WATCH");
        table.AddColumn("Activity");
        table.AddColumn("Pending");
        table.AddColumn("Last indexed");
        table.AddColumn("Status");
        foreach (var entry in fleet)
        {
            table.AddRow(
                entry.NameCollision == true
                    ? $"{entry.Name.EscapeMarkup()} [yellow](name shared)[/]"
                    : entry.Name.EscapeMarkup(),
                entry.RootPath.EscapeMarkup(),
                entry.StoreKey.EscapeMarkup(),
                FormatDaemons(entry.Daemons, DaemonHeartbeat.DefaultKind),
                FormatDaemons(entry.Daemons, DaemonHeartbeat.ServeWatchKind),
                FormatActivity(entry.Daemons),
                entry.PendingChunks?.ToString() ?? "n/a",
                entry.LastIndexedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "n/a",
                entry.Stale ? "[yellow]stale[/]" : "[green]ok[/]");
        }

        AnsiConsole.Write(table);

        foreach (var stale in fleet.Where(entry => entry.Stale))
        {
            Console.Error.WriteLine(
                $"warning: workspace '{stale.Name}' is stale (storeKey '{stale.StoreKey}'): " +
                $"root path '{stale.RootPath}' does not exist; run 'squeezr drop --store-key {stale.StoreKey}' " +
                "to remove this entry and its store.");
        }

        return 0;
    }

    // Every process of the kind this store names, one line per pid. The role mark is carried only where the claim
    // names an owner at all: a store whose beats predate the claim, or whose marker is gone, shows plain pids
    // rather than inventing an observer out of the absence of evidence.
    private static string FormatDaemons(IReadOnlyList<FleetDaemonProcess> daemons, string kind)
    {
        var rows = daemons.Where(daemon => daemon.Kind == kind).ToList();
        if (rows.Count == 0)
        {
            return "—";
        }

        var claimsOwner = daemons.Any(daemon => daemon.Owner);
        return string.Join(Environment.NewLine, rows.Select(row => FormatDaemonRow(row, claimsOwner)));
    }

    private static string FormatDaemonRow(FleetDaemonProcess daemon, bool claimsOwner)
    {
        var role = !claimsOwner
            ? string.Empty
            : daemon.Owner ? " [green](owner)[/]" : " [dim](observer)[/]";
        return daemon.Status switch
        {
            FleetDaemonState.AliveStatus =>
                $"pid {daemon.Pid}, up {FormatUptime(daemon.UptimeSeconds!.Value)}{role}",
            FleetDaemonState.DeadStatus => $"dead (pid {daemon.Pid})",
            _ => "—"
        };
    }

    // One line per live process, roster order; the executor tail repeats what the beat's evidence named and is
    // never upgraded into a verdict of its own. A live process between passes answers idle, and a roster with
    // nothing alive answers idle? — the question mark is the honesty: a beat that aged out or a pid that is gone
    // is no evidence of work, and this cell never renders one.
    internal static string FormatActivity(IReadOnlyList<FleetDaemonProcess> daemons)
    {
        var lines = daemons.Where(daemon => daemon.IsAlive).Select(FormatActivityRow).ToList();
        return lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : daemons.Count > 0 ? "idle?" : "—";
    }

    private static string FormatActivityRow(FleetDaemonProcess daemon) =>
        daemon.Phase is not { } phase ? "idle" : FormatWork(daemon, phase);

    private static string FormatWork(FleetDaemonProcess daemon, string phase)
    {
        var counter = daemon.TotalItems is { } total && total > 0 && daemon.DoneItems is { } done
            ? $" · {FormatCounter(phase, done, total)}"
            : string.Empty;
        var executor = daemon.Executor is not { } device
            ? string.Empty
            : daemon.Provider is { } provider
                ? $" · {provider.EscapeMarkup()} · {device.EscapeMarkup()}"
                : $" · {device.EscapeMarkup()}";
        return $"{FormatPhase(phase)} {(daemon.Repo ?? "unknown").EscapeMarkup()}{counter}{executor}";
    }

    private static string FormatPhase(string phase) => phase switch
    {
        "walk" => "walking",
        "files" => "indexing",
        "embeddings" => "embedding",
        _ => phase.EscapeMarkup(),
    };

    private static string FormatCounter(string phase, int done, int total) => phase switch
    {
        "files" => $"files {done}/{total}",
        "embeddings" => $"chunks {done}/{total}",
        _ => $"{done}/{total}",
    };

    private static string FormatUptime(long totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes}m";
        }

        return $"{(int)span.TotalSeconds}s";
    }
}
