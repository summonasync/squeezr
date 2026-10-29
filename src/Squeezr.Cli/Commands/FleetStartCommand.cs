using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetStartCommand : Command<FleetStartCommand.Settings>
{
    private readonly SupervisionState _state;

    public FleetStartCommand() : this(new SupervisionState())
    {
    }

    internal FleetStartCommand(SupervisionState state) => _state = state;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<storeKey>")]
        public string StoreKey { get; set; } = string.Empty;

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var entry = FleetStoreKeyResolver.Require(settings.StoreKey);
            _state.SetDesire(entry.StoreKey, WatchDesire.Running);
            var watchd = FleetWatchdProbe.Probe();
            var health = WatchStoreHealth.Probe(SqueezrPaths.StoreDir(entry.RootPath), DateTime.UtcNow);
            Report(
                new FleetStartResult(
                    entry.Name,
                    entry.StoreKey,
                    WatchDesire.Running.ToString().ToLowerInvariant(),
                    watchd,
                    health.State.ToString().ToLowerInvariant()),
                settings.Json);
            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void Report(FleetStartResult result, bool json)
    {
        if (json)
        {
            JsonOutput.WriteLine(result);
            return;
        }

        AnsiConsole.MarkupLine(
            $"[green]watch[/]: desired running for '{result.Name.EscapeMarkup()}' ({result.StoreKey.EscapeMarkup()})");
        if (!result.Watchd.Running)
        {
            Console.Error.WriteLine(
                "warning: watchd is not running — the desire is recorded and applies when it starts ('squeezr watchd').");
            return;
        }

        AnsiConsole.MarkupLine(result.IndexerState == WatchIndexerState.Alive.ToString().ToLowerInvariant()
            ? $"watchd (pid {result.Watchd.Pid}) supervises it and the watch daemon is already up."
            : $"watchd (pid {result.Watchd.Pid}) will bring it up within its poll interval.");
    }
}
