using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetStopCommand : AsyncCommand<FleetStopCommand.Settings>
{
    private readonly SupervisionState _state;

    public FleetStopCommand() : this(new SupervisionState())
    {
    }

    internal FleetStopCommand(SupervisionState state) => _state = state;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<storeKey>")]
        public string StoreKey { get; set; } = string.Empty;

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var entry = FleetStoreKeyResolver.RequireResolvable(
                FleetStoreKeyResolver.Require(settings.StoreKey), "stop");
            // The desire lands before any kill: a supervisor sweeping between the two would otherwise
            // see a dead daemon under a still-running desire and respawn the process we are stopping.
            _state.SetDesire(entry.StoreKey, WatchDesire.Stopped);
            var storeDir = SqueezrPaths.StoreDir(entry);
            var now = DateTime.UtcNow;
            var (targets, observers, noOwnerReason) = ResolveKillTargets(storeDir, now);


            if (targets.Count == 0)
            {
                Report(
                    new FleetStopResult(
                        entry.Name,
                        entry.StoreKey,
                        KindFill(null),
                        observers.Count,
                        WatchDesire.Stopped.ToString().ToLowerInvariant()),
                    settings.Json);
                if (!settings.Json)
                {
                    Console.Error.WriteLine(
                        noOwnerReason
                        ?? (observers.Count > 0
                            ? $"warning: no watch owner to stop for workspace '{entry.Name}'; " +
                              $"{observers.Count} observer(s) left running (fleet stop never kills observers)."
                            : $"warning: no running daemons for workspace '{entry.Name}'."));
                }

                return 0;
            }

            foreach (var (_, heartbeat) in targets)
            {
                DaemonProcessGuard.EnsureSqueezrProcess(heartbeat);
            }

            var results = new List<FleetStopKindResult>();
            foreach (var (kind, heartbeat) in targets)
            {
                var detail = await DaemonTerminator.StopAsync(heartbeat.Pid, cancellationToken);
                results.Add(new FleetStopKindResult(kind, "stopped", heartbeat.Pid, detail));
            }

            results = KindFill(results)
                .OrderBy(result => result.Kind == DaemonHeartbeat.DefaultKind ? 0 : 1)
                .ToList();

            Report(
                new FleetStopResult(
                    entry.Name,
                    entry.StoreKey,
                    results,
                    observers.Count,
                    WatchDesire.Stopped.ToString().ToLowerInvariant()),
                settings.Json);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 1;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    // The kill surface follows the claim (070/T5): watch.owner names the process that runs the watch loop, the
    // roster only proves it still lives here, and everything else beating on the store is an observer, never a
    // target — the owner is whoever holds the claim, a client's live `serve --watch` included. A wedged owner
    // (stale beat, live pid) stays a target: stopping the named process is the supported recovery for a store
    // whose lock never came back. With no marker the store is in pre-070 shape, where the per-kind heartbeat
    // was the only name an indexer ever had.
    internal static (List<(string Kind, DaemonHeartbeat Heartbeat)> Targets,
        List<DaemonHeartbeat> Observers,
        string? NoOwnerReason) ResolveKillTargets(string storeDir, DateTime now)
    {
        var roster = DaemonHeartbeatReader.TryReadAll(storeDir);
        var owner = WatchOwnerMarker.TryRead(storeDir);
        if (owner is null)
        {
            var legacy = new List<(string, DaemonHeartbeat)>();
            foreach (var kind in (string[])[DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind])
            {
                var heartbeat = DaemonHeartbeatReader.TryRead(storeDir, kind);
                if (heartbeat is not null && DaemonHeartbeatReader.IsAlive(heartbeat, now))
                {
                    legacy.Add((kind, heartbeat));
                }
            }

            var legacyObservers = roster
                .Where(beat => DaemonHeartbeatReader.IsAlive(beat, now))
                .Where(beat => legacy.All(pair => pair.Item2.Pid != beat.Pid))
                .ToList();
            return (legacy, legacyObservers, null);
        }

        var targets = new List<(string, DaemonHeartbeat)>();
        var sameHost = string.Equals(owner.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        string? noOwnerReason = null;
        if (!sameHost)
        {
            noOwnerReason =
                $"warning: the watch claim for this store is held on host '{owner.Host}' (pid {owner.Pid}); " +
                "nothing on this machine runs the loop and nothing here stops a foreign pid.";
        }
        else if (DaemonHeartbeatReader.IsProcessAlive(owner.Pid))
        {
            targets.Add((owner.Kind, owner.AsHeartbeat()));
        }
        else
        {
            noOwnerReason =
                $"warning: the watch claim names pid {owner.Pid}, which is gone; its beat may still read alive " +
                "until the stale window passes, and a surviving observer takes the store over.";
        }

        var observers = roster
            .Where(beat => DaemonHeartbeatReader.IsAlive(beat, now) && !owner.OwnsBeat(beat))
            .ToList();
        return (targets, observers, noOwnerReason);
    }

    private static List<FleetStopKindResult> KindFill(List<FleetStopKindResult>? stopped)
    {
        var results = new List<FleetStopKindResult>();
        foreach (var kind in (string[])[DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind])
        {
            if (stopped?.All(result => result.Kind != kind) is not false)
            {
                results.Add(new FleetStopKindResult(kind, "not-running", null, null));
            }
            else
            {
                results.Add(stopped!.First(result => result.Kind == kind));
            }
        }

        return results;
    }

    private static void Report(FleetStopResult result, bool json)
    {
        if (json)
        {
            JsonOutput.WriteLine(result);
            return;
        }

        foreach (var kindResult in result.Results)
        {
            if (kindResult.Status == "stopped")
            {
                AnsiConsole.MarkupLine(
                    $"[green]{kindResult.Kind.EscapeMarkup()}[/] (pid {kindResult.Pid}): stopped ({kindResult.Detail})");
            }
            else
            {
                AnsiConsole.MarkupLine($"[grey]{kindResult.Kind.EscapeMarkup()}: not running[/]");
            }
        }

        AnsiConsole.MarkupLine(
            "[grey]desired state: stopped — watchd will not bring it back until 'squeezr fleet start <store-key>'[/]");
    }
}
