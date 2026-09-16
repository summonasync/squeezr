using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetCommand : Command<FleetCommand.Settings>
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
        table.AddColumn("Pending");
        table.AddColumn("Last indexed");
        table.AddColumn("Status");
        foreach (var entry in fleet)
        {
            table.AddRow(
                entry.Name.EscapeMarkup(),
                entry.RootPath.EscapeMarkup(),
                entry.StoreKey.EscapeMarkup(),
                FormatDaemon(entry.Watch),
                FormatDaemon(entry.ServeWatch),
                entry.PendingChunks?.ToString() ?? "n/a",
                entry.LastIndexedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "n/a",
                entry.Stale ? "[yellow]stale[/]" : "[green]ok[/]");
        }

        AnsiConsole.Write(table);

        foreach (var stale in fleet.Where(entry => entry.Stale))
        {
            Console.Error.WriteLine(
                $"warning: workspace '{stale.Name}' is stale: root path '{stale.RootPath}' does not exist; {StaleDropHint}.");
        }

        return 0;
    }

    private static string FormatDaemon(FleetDaemonState daemon) => daemon.Status switch
    {
        FleetDaemonState.AliveStatus => $"pid {daemon.Pid}, up {FormatUptime(daemon.UptimeSeconds!.Value)}",
        FleetDaemonState.DeadStatus => $"dead (pid {daemon.Pid})",
        _ => "—",
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
