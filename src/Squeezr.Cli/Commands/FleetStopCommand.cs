using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetStopCommand : AsyncCommand<FleetStopCommand.Settings>
{
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
            var entry = FleetStoreKeyResolver.Require(settings.StoreKey);
            var storeDir = SqueezrPaths.StoreDir(entry.RootPath);
            var now = DateTime.UtcNow;
            var alive = ProbeAlive(storeDir, now);

            if (alive.Count == 0)
            {
                Report(
                    new FleetStopResult(
                        entry.Name,
                        entry.StoreKey,
                        [
                            new FleetStopKindResult(DaemonHeartbeat.DefaultKind, "not-running", null, null),
                            new FleetStopKindResult(DaemonHeartbeat.ServeWatchKind, "not-running", null, null),
                        ]),
                    settings.Json);
                if (!settings.Json)
                {
                    Console.Error.WriteLine($"warning: no running daemons for workspace '{entry.Name}'.");
                }

                return 0;
            }

            foreach (var (_, heartbeat) in alive)
            {
                DaemonProcessGuard.EnsureSqueezrProcess(heartbeat);
            }

            var results = new List<FleetStopKindResult>();
            foreach (var (kind, heartbeat) in alive)
            {
                var detail = await DaemonTerminator.StopAsync(heartbeat.Pid, cancellationToken);
                results.Add(new FleetStopKindResult(kind, "stopped", heartbeat.Pid, detail));
            }

            foreach (var kind in new[] { DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind })
            {
                if (results.All(result => result.Kind != kind))
                {
                    results.Add(new FleetStopKindResult(kind, "not-running", null, null));
                }
            }

            results = results
                .OrderBy(result => result.Kind == DaemonHeartbeat.DefaultKind ? 0 : 1)
                .ToList();

            Report(new FleetStopResult(entry.Name, entry.StoreKey, results), settings.Json);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 1;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static List<(string Kind, DaemonHeartbeat Heartbeat)> ProbeAlive(string storeDir, DateTime now)
    {
        var alive = new List<(string Kind, DaemonHeartbeat Heartbeat)>();
        foreach (var kind in new[] { DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind })
        {
            var heartbeat = DaemonHeartbeatReader.TryRead(storeDir, kind);
            if (heartbeat is not null && DaemonHeartbeatReader.IsAlive(heartbeat, now))
            {
                alive.Add((kind, heartbeat));
            }
        }

        return alive;
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
    }
}
