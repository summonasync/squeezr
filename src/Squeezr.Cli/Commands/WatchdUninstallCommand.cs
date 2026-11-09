using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WatchdUninstallCommand : AsyncCommand<WatchdUninstallCommand.Settings>
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
            return await RemoveAsync(AutostartEnvironment.CreateApplier(platform, new AutostartProcessRunner()), cancellationToken);
        }
        catch (Exception ex) when (ex is AutostartException or ArgumentException or WorkspaceException)
        {
            return CommandError.Report(ex, json: false);
        }
    }

    internal static async Task<int> RemoveAsync(
        IWatchdAutostartApplier applier,
        CancellationToken cancellationToken)
    {
        if (await applier.UninstallAsync(cancellationToken))
        {
            AnsiConsole.MarkupLine($"[green]watchd autostart removed[/] — {applier.ManagedTarget.EscapeMarkup()}");
            return 0;
        }

        // Nothing of ours to take away is not a failure: uninstall stays repeatable, but saying so
        // out loud on stderr is what keeps a silent exit code from reading as "it was removed".
        Console.Error.WriteLine(
            $"warning: no squeezr watchd autostart entry at {applier.ManagedTarget}; nothing was removed.");
        return 0;
    }
}
