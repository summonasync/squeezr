using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Squeezr.Cli.Daemons;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WatchdCommand : Command<WatchdCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onStop = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onStop;
        try
        {
            SqueezrHome.RequireHabitableProfileHome();
            using var watchdLock = new WatchdLock();
            var outcome = watchdLock.TryAcquire();
            if (!outcome.Acquired)
            {
                var holder = outcome.OwnerPid is { } pid ? $"pid {pid}" : "another process";
                Console.Error.WriteLine(
                    $"error: watchd is already running ({holder} holds {WatchdPaths.LockPath}); " +
                    "stop that instance first, or remove the lock once you have made sure it is gone.");
                return 1;
            }

            var loop = new SupervisorLoop(
                new DaemonLauncherAdapter(new DaemonLauncher()), new SupervisionState(), new CrashLoopGuard());
            if (AnsiConsole.Profile.Capabilities.Interactive)
            {
                var live = new LiveWatchdStatus();
                AnsiConsole.Live(live.Render()).Start(liveContext =>
                {
                    live.Bind(liveContext);
                    loop.Run(cts.Token, live.Update);
                });
            }
            else
            {
                loop.Run(cts.Token, new PlainWatchdStatus().Update);
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onStop;
        }
    }

    private sealed class DaemonLauncherAdapter(DaemonLauncher launcher) : IWatchDaemonLauncher
    {
        public IWatchDaemonHandle StartWatch(string rootPath)
        {
            var binaryPath = launcher.DaemonBinaryPath();
            var process = launcher.StartWatch(rootPath);
            return new ProcessHandle(binaryPath, process);
        }
    }

    private sealed class ProcessHandle(string binaryPath, Process process) : IWatchDaemonHandle
    {
        public int Id => process.Id;

        public string BinaryPath => binaryPath;

        public DateTime? StartedUtc
        {
            get
            {
                try
                {
                    return process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return null;
                }
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void Stop()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public void Dispose() => process.Dispose();
    }

    private sealed class LiveWatchdStatus : IWatchdStatus
    {
        private readonly object _gate = new();
        private LiveDisplayContext? _context;
        private SupervisorSweep? _sweep;

        public void Bind(LiveDisplayContext context) => _context = context;

        public void Update(SupervisorSweep sweep)
        {
            lock (_gate)
            {
                _sweep = sweep;
            }

            Redraw();
        }

        public IRenderable Render()
        {
            lock (_gate)
            {
                var table = new Table().Expand().BorderColor(Color.Grey);
                table.AddColumn("workspace");
                table.AddColumn("desire");
                table.AddColumn("indexer");
                table.AddColumn("supervision");
                table.AddColumn("detail");
                foreach (var store in _sweep?.Stores ?? [])
                {
                    table.AddRow(
                        Markup.Escape(store.Name),
                        Markup.Escape(store.Desire.ToString().ToLowerInvariant()),
                        Markup.Escape(store.IndexerState.ToString().ToLowerInvariant()),
                        Colorize(store.Action),
                        Markup.Escape(store.Detail ?? string.Empty));
                }

                return table;
            }
        }

        private static string Colorize(SupervisionAction action) => action switch
        {
            SupervisionAction.Started or SupervisionAction.LeftRunning => $"[green]{action}[/]",
            SupervisionAction.DriftRestarted => $"[yellow]{action}[/]",
            SupervisionAction.StartFailed or SupervisionAction.BlockedByCrashLoop or SupervisionAction.StoppedByDesire
                => $"[red]{action}[/]",
            _ => $"[grey]{action}[/]",
        };

        private void Redraw()
        {
            if (_context is null)
            {
                return;
            }

            _context.UpdateTarget(Render());
            _context.Refresh();
        }
    }

    private sealed class PlainWatchdStatus : IWatchdStatus
    {
        private readonly Dictionary<string, string> _lastLines = [];

        public void Update(SupervisorSweep sweep)
        {
            foreach (var store in sweep.Stores)
            {
                var line = Describe(store);
                if (_lastLines.TryGetValue(store.StoreKey, out var previous) && previous == line)
                {
                    continue;
                }

                _lastLines[store.StoreKey] = line;
                Console.WriteLine($"{Stamp()} {line}");
            }
        }

        private static string Describe(StoreSupervision store) => store.Action switch
        {
            SupervisionAction.Started => $"started watch {store.Name} (pid {store.ManagedPid})",
            SupervisionAction.DriftRestarted =>
                $"restarted watch {store.Name} after an upgrade (pid {store.ManagedPid})",
            SupervisionAction.LeftRunning => $"watch {store.Name} is running (pid {store.ManagedPid})",
            SupervisionAction.StoppedByDesire => $"stopped watch {store.Name}: desired stopped",
            SupervisionAction.LeftStopped => $"watch {store.Name} stays stopped",
            SupervisionAction.SkippedLiveOwner => store.OwnerPid is { } pid
                ? $"watch {store.Name} is managed elsewhere (pid {pid})"
                : $"watch {store.Name} is managed elsewhere",
            SupervisionAction.BlockedByCrashLoop => $"watch {store.Name} is wedged: {store.Detail}",
            SupervisionAction.StartFailed => $"watch {store.Name} failed to start: {store.Detail}",
            SupervisionAction.SkippedRoot => $"watch {store.Name} skipped: {store.Detail}",
            _ => $"watch {store.Name}: {store.Action}",
        };

        private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");
    }

    private interface IWatchdStatus
    {
        void Update(SupervisorSweep sweep);
    }
}
