using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WatchdCommand : Command<WatchdCommand.Settings>
{
    // Invariant: this has to stay non-zero. Restart=on-failure (systemd) and KeepAlive with
    // SuccessfulExit=false (launchd) bring a program back only when it left non-zero, so a zero here
    // means a retired supervisor is never replaced. The value sits outside this command's own 0 and 1
    // (clean stop, operator-facing refusal) and outside the watchd-installed ladder of 1..3, and 75 is
    // sysexits' EX_TEMPFAIL — a temporary failure the starter is invited to retry.
    public const int UpgradeExitCode = 75;

    public sealed class Settings : CommandSettings
    {
        [CommandOption(WatchdUnitGenerator.AutostartFlag)]
        [Description("Started by the autostart entry: retire after an upgrade and let the entry start the new binary")]
        public bool Autostart { get; init; }
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

            var launcher = new DaemonLauncher();
            var loop = new SupervisorLoop(
                new DaemonLauncherAdapter(launcher),
                new SupervisionState(),
                new CrashLoopGuard(),
                SelfCheckFor(launcher, settings.Autostart));
            var exitReason = SupervisorExitReason.Cancelled;
            if (AnsiConsole.Profile.Capabilities.Interactive)
            {
                var live = new LiveWatchdStatus();
                AnsiConsole.Live(live.Render()).Start(liveContext =>
                {
                    live.Bind(liveContext);
                    exitReason = loop.Run(cts.Token, live.Update);
                });
            }
            else
            {
                exitReason = loop.Run(cts.Token, new PlainWatchdStatus().Update);
            }

            return exitReason == SupervisorExitReason.OwnBinaryDrifted ? UpgradeExitCode : 0;
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

    private static SupervisorSelfCheck? SelfCheckFor(DaemonLauncher launcher, bool autostart)
    {
        if (!autostart)
        {
            return null;
        }

        AutostartPlatform platform;
        try
        {
            platform = AutostartEnvironment.CurrentPlatform;
        }
        catch (AutostartException ex)
        {
            // Nothing is known to start this process again on a platform whose entry we cannot name,
            // so an upgrade retire would take the supervisor down for good. It keeps what it has.
            Console.Error.WriteLine(
                $"warning: {ex.Message} — {WatchdUnitGenerator.AutostartFlag} cannot retire this process, " +
                "watchd keeps running the binary it started on");
            return null;
        }

        return new SupervisorSelfCheck(
            launcher.DaemonBinaryPath(),
            OwnProcessStartUtc(),
            WatchdRestartPolicy.RestartsOnUnsuccessfulExit(platform));
    }

    private static DateTime? OwnProcessStartUtc()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class DaemonLauncherAdapter(DaemonLauncher launcher) : IWatchDaemonLauncher
    {
        public IWatchDaemonHandle StartWatch(string rootPath)
        {
            var binaryPath = launcher.DaemonBinaryPath();
            var process = launcher.StartSupervisedWatch(rootPath);
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

                if (_sweep?.Fault is { } fault)
                {
                    table.AddRow(string.Empty, string.Empty, string.Empty, "[red]sweep failed[/]", Markup.Escape(fault));
                }

                if (_sweep?.StateWarning is { } warning)
                {
                    table.AddRow(
                        string.Empty, string.Empty, string.Empty, "[yellow]state unreadable[/]", Markup.Escape(warning));
                }

                if (_sweep?.OwnDrift is { } drift)
                {
                    table.AddRow(
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "[yellow]retiring[/]",
                        Markup.Escape(drift.Reason));
                }

                return table;
            }
        }

        private static string Colorize(SupervisionAction action) => action switch
        {
            SupervisionAction.Started or SupervisionAction.LeftRunning => $"[green]{action}[/]",
            SupervisionAction.DriftRestarted => $"[yellow]{action}[/]",
            SupervisionAction.StartFailed or SupervisionAction.BlockedByCrashLoop or SupervisionAction.StoppedByDesire
                or SupervisionAction.ProbeFailed => $"[red]{action}[/]",
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
        private string? _lastFault;
        private string? _lastStateWarning;

        public void Update(SupervisorSweep sweep)
        {
            if (sweep.Fault is { } fault && _lastFault != fault)
            {
                _lastFault = fault;
                Console.WriteLine($"{Stamp()} registry sweep failed: {fault}");
            }

            if (sweep.StateWarning is { } warning && _lastStateWarning != warning)
            {
                _lastStateWarning = warning;
                Console.WriteLine($"{Stamp()} {warning}");
            }

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

            if (sweep.OwnDrift is { } drift)
            {
                Console.WriteLine($"{Stamp()} {drift.Reason}");
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
            SupervisionAction.ProbeFailed => $"watch {store.Name} could not be checked: {store.Detail}",
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
