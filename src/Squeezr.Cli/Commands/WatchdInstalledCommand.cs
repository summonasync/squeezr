using Spectre.Console.Cli;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

// Read-only by contract: it asks the platform what is registered and reports the answer, including the
// answers the platform would not give. Nothing here starts, stops or rewrites an entry — a status
// command that fixes things on the way past is how an operator loses track of what was actually wrong.
public sealed class WatchdInstalledCommand : AsyncCommand<WatchdInstalledCommand.Settings>
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
            return await ReportAsync(
                AutostartEnvironment.CreateApplier(
                    AutostartEnvironment.CurrentPlatform,
                    new AutostartProcessRunner()),
                FleetWatchdProbe.Probe(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is AutostartException or ArgumentException or WorkspaceException
                                   or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, json: false);
        }
    }

    // The seam every case is measured through: the platform is already folded into the answers the
    // applier returns, so this takes the entry to read and the supervisor state to report beside it.
    internal static async Task<int> ReportAsync(
        IWatchdAutostartApplier applier,
        FleetWatchdState watchd,
        CancellationToken cancellationToken)
    {
        var report = await WatchdInstalledProbe.ReadAsync(applier, watchd, cancellationToken);
        foreach (var line in WatchdInstalledLines.Of(report))
        {
            Console.WriteLine(line);
        }

        return WatchdInstalledVerdict.ExitCodeFor(report.State);
    }
}
