using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class FleetRestartCommand : AsyncCommand<FleetRestartCommand.Settings>
{
    private readonly DaemonLauncher _launcher;

    public FleetRestartCommand() : this(new DaemonLauncher())
    {
    }

    internal FleetRestartCommand(DaemonLauncher launcher) => _launcher = launcher;

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

            string previousStatus;
            int? previousPid = null;
            if (watchAlive)
            {
                DaemonProcessGuard.EnsureSqueezrProcess(watch!);
                previousStatus = await DaemonTerminator.StopAsync(watch.Pid, cancellationToken);
                previousPid = watch.Pid;
            }
            else
            {
                previousStatus = "not-running";
            }

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

            Report(new FleetRestartResult(entry.Name, entry.StoreKey, previousStatus, previousPid, pid), settings.Json);
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
