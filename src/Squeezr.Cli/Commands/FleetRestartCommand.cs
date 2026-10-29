using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetRestartCommand : AsyncCommand<FleetRestartCommand.Settings>
{
    private readonly DaemonLauncher _launcher;
    private readonly SupervisionState _state;
    private readonly CrashLoopGuard _guard;

    public FleetRestartCommand() : this(new DaemonLauncher())
    {
    }

    internal FleetRestartCommand(DaemonLauncher launcher) : this(launcher, new SupervisionState(), new CrashLoopGuard())
    {
    }

    internal FleetRestartCommand(DaemonLauncher launcher, SupervisionState state, CrashLoopGuard guard)
    {
        _launcher = launcher;
        _state = state;
        _guard = guard;
    }

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
            if (!Directory.Exists(entry.RootPath))
            {
                throw new WorkspaceException(
                    $"Cannot restart workspace '{entry.Name}': root path '{entry.RootPath}' does not exist; " +
                    $"remove the stale entry with 'squeezr drop --store-key {entry.StoreKey}'.");
            }

            var storeDir = SqueezrPaths.StoreDir(entry.RootPath);
            var now = DateTime.UtcNow;
            var owner = WatchOwnerMarker.TryRead(storeDir);
            string previousStatus;
            int? previousPid = null;

            if (owner is not null)
            {
                // The restart follows the claim (070/T5): watch.owner names the process that runs the loop, and the
                // heartbeat files are only the pre-claim shape's fallback. Observers beating beside the owner are
                // MCP clients' live servers — a restart never stops one of them, and a fresh watch claim on this
                // store belongs to whoever the marker points at.
                if (!string.Equals(owner.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceException(
                        $"Cannot restart workspace '{entry.Name}': the watch claim is held on host '{owner.Host}' " +
                        $"(pid {owner.Pid}); restart the watch loop there, not from this machine.");
                }

                if (DaemonHeartbeatReader.IsProcessAlive(owner.Pid))
                {
                    if (owner.Kind == DaemonHeartbeat.ServeWatchKind)
                    {
                        throw new WorkspaceException(
                            $"Cannot restart workspace '{entry.Name}': a serve --watch daemon is running (pid {owner.Pid}) " +
                            "but it is owned by the MCP client and cannot be restarted from the CLI. " +
                            "Run 'squeezr mcp install' or restart your agent.");
                    }

                    var heartbeat = owner.AsHeartbeat();
                    DaemonProcessGuard.EnsureSqueezrProcess(heartbeat);
                    previousStatus = await DaemonTerminator.StopAsync(owner.Pid, cancellationToken);
                    previousPid = owner.Pid;
                }
                else
                {
                    previousStatus = "not-running";
                }
            }
            else
            {
                var watch = DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.DefaultKind);
                var watchAlive = watch is not null && DaemonHeartbeatReader.IsAlive(watch, now);
                var serveWatch = DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.ServeWatchKind);
                var serveWatchAlive = serveWatch is not null && DaemonHeartbeatReader.IsAlive(serveWatch, now);

                if (!watchAlive && serveWatchAlive)
                {
                    throw new WorkspaceException(
                        $"Cannot restart workspace '{entry.Name}': a serve --watch daemon is running (pid {serveWatch!.Pid}) " +
                        "but it is owned by the MCP client and cannot be restarted from the CLI. " +
                        "Run 'squeezr mcp install' or restart your agent.");
                }

                if (watchAlive)
                {
                    DaemonProcessGuard.EnsureSqueezrProcess(watch!);
                    previousStatus = await DaemonTerminator.StopAsync(watch!.Pid, cancellationToken);
                    previousPid = watch.Pid;
                }
                else
                {
                    previousStatus = "not-running";
                }
            }

            // fleet restart is the documented way out of a crash-loop wedge, and it re-arms the store:
            // the fresh daemon must not inherit the blocked restart budget of the run it replaces.
            _state.SetDesire(entry.StoreKey, WatchDesire.Running);
            _guard.Reset(entry.StoreKey);

            int pid;
            try
            {
                using var process = _launcher.StartWatch(entry.RootPath);
                pid = process.Id;
            }
            catch (WorkspaceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkspaceException($"Failed to start the watch daemon: {ex.Message}", innerException: ex);
            }

            Report(
                new FleetRestartResult(
                    entry.Name,
                    entry.StoreKey,
                    previousStatus,
                    previousPid,
                    pid,
                    WatchDesire.Running.ToString().ToLowerInvariant()),
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

    private static void Report(FleetRestartResult result, bool json)
    {
        if (json)
        {
            JsonOutput.WriteLine(result);
            return;
        }

        if (result.PreviousStatus == "not-running")
        {
            AnsiConsole.MarkupLine($"[green]watch[/]: started (pid {result.Pid})");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]watch[/]: restarted (pid {result.PreviousPid} stopped {result.PreviousStatus}, new pid {result.Pid})");
        }
    }
}
