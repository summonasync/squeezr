using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Cli.Output;
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
                new DaemonLauncher().DaemonBinaryPath(),
                AutostartEnvironment.ExplicitSqueezrHome(),
                AutostartEnvironment.CreateApplier(platform, new AutostartProcessRunner()),
                cancellationToken);
        }
        catch (Exception ex) when (ex is AutostartException or ArgumentException or WorkspaceException)
        {
            return CommandError.Report(ex, json: false);
        }
    }

    internal static async Task<int> ApplyAsync(
        AutostartPlatform platform,
        string binaryPath,
        string? squeezrHome,
        IWatchdAutostartApplier applier,
        CancellationToken cancellationToken)
    {
        var unitText = WatchdUnitGenerator.Generate(platform, binaryPath, squeezrHome);
        await applier.InstallAsync(unitText, cancellationToken);
        var carried = squeezrHome is null ? string.Empty : $" (with {SqueezrHome.HomeEnvironmentVariable}={squeezrHome})";
        AnsiConsole.MarkupLine($"[green]watchd autostart installed[/] — {applier.ManagedTarget.EscapeMarkup()}{carried.EscapeMarkup()}");
        return 0;
    }
}
