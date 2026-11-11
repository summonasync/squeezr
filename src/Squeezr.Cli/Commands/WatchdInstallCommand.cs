using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WatchdInstallCommand : AsyncCommand<WatchdInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            SqueezrHome.RequireHabitableProfileHome();
            var platform = AutostartEnvironment.CurrentPlatform;
            return await ApplyAsync(
                platform,
                new DaemonLauncher().WatchdLaunchPair(),
                AutostartEnvironment.ExplicitSqueezrHome(),
                AutostartEnvironment.CreateApplier(platform, new AutostartProcessRunner()),
                cancellationToken);
        }
        catch (Exception ex) when (ex is AutostartException or ArgumentException or WorkspaceException
                                   or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, json: false);
        }
    }

    // The guard belongs to the seam, not to the verb: every caller that hands over a launch pair (the
    // command, the checks T4/T9 build on top of this) gets the same check on the program it is about to
    // bake in, and none can step around it. The payload token is not guarded — the init system never
    // execs it, the program it sits behind loads it.
    internal static async Task<int> ApplyAsync(
        AutostartPlatform platform,
        WatchdRunCommand launch,
        string? squeezrHome,
        IWatchdAutostartApplier applier,
        CancellationToken cancellationToken)
    {
        Launchability.Require(platform, launch.Executable);
        var unitText = WatchdUnitGenerator.Generate(platform, launch, squeezrHome);
        // The lock is read before the entry is applied, not after: once the init system has the unit up,
        // its own watchd holds the lock and no read can tell that instance from one the operator started
        // by hand. The read is all this does — install takes nothing, steals nothing and kills nothing,
        // because whose instance should own the singleton is the operator's call, not this command's.
        var lockPath = WatchdPaths.LockPathUnder(squeezrHome);
        var heldBy = LiveLockHolder(lockPath);
        await applier.InstallAsync(unitText, cancellationToken);
        if (heldBy is { } pid)
        {
            Console.Error.WriteLine(ManualWatchdNotice(platform, lockPath, pid));
        }

        var carried = squeezrHome is null ? string.Empty : $" (with {SqueezrHome.HomeEnvironmentVariable}={squeezrHome})";
        AnsiConsole.MarkupLine($"[green]watchd autostart installed[/] — {applier.ManagedTarget.EscapeMarkup()}{carried.EscapeMarkup()}");
        return 0;
    }

    private static int? LiveLockHolder(string lockPath)
    {
        var watchd = FleetWatchdProbe.Probe(lockPath, DaemonHeartbeatReader.IsProcessAlive);
        return watchd.Running ? watchd.Pid : null;
    }

    // The chain the operator would otherwise have to reconstruct from `systemctl --user status`: the
    // singleton is taken, so the instance the entry starts refuses and leaves non-zero, and what the
    // entry then does about that is written in the unit text install just generated.
    private static string ManualWatchdNotice(AutostartPlatform platform, string lockPath, int pid) =>
        $"warning: a live watchd (pid {pid}) already holds {lockPath} — {Collision(platform)} " +
        $"Stop that instance and run install again{RestartHint(platform)}.";

    private static string Collision(AutostartPlatform platform) => platform switch
    {
        AutostartPlatform.SystemdUser =>
            "the unit starts now and its instance will refuse the lock, so systemd restarts it on " +
            "failure until the start limit trips and the unit is left failed.",
        AutostartPlatform.LaunchAgent =>
            "the agent starts now and its instance will refuse the lock, so launchd respawns it on " +
            "every unsuccessful exit.",
        _ =>
            "the task's instance will refuse the lock at every logon until it is gone.",
    };

    // Restarting is not free once the limit has tripped: systemd keeps refusing the start as "Start
    // request repeated too quickly" until the window the unit declares has passed, and reset-failed
    // flushes that counter instead of waiting (systemd.unit(5)). The line names both, because an exit
    // the very next second refuses is not an exit the operator can use.
    private static string RestartHint(AutostartPlatform platform)
    {
        if (platform != AutostartPlatform.SystemdUser)
        {
            return string.Empty;
        }

        var unit = WatchdUnitGenerator.DefaultUnitName;
        var window = WatchdUnitGenerator.StartLimitWindowSeconds;
        return $", or restart the unit once it is gone: systemctl --user reset-failed {unit} && " +
               $"systemctl --user restart {unit} — until the {window}-second window the unit declares " +
               "has passed, the limit keeps refusing the start";
    }
}
