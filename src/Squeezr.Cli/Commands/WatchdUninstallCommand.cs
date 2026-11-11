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
        catch (Exception ex) when (ex is AutostartException or ArgumentException or WorkspaceException
                                   or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, json: false);
        }
    }

    internal static async Task<int> RemoveAsync(
        IWatchdAutostartApplier applier,
        CancellationToken cancellationToken)
    {
        var removal = await applier.UninstallAsync(cancellationToken);
        foreach (var warning in removal.Warnings)
        {
            // The removal stands even when the init system refused to answer: the managed file is
            // gone, so this is the operator's notice about what could not be checked, not a failure.
            Console.Error.WriteLine($"warning: {warning}");
        }

        if (removal.WasRemoved)
        {
            AnsiConsole.MarkupLine($"[green]watchd autostart removed[/] — {applier.ManagedTarget.EscapeMarkup()}");
            return 0;
        }

        // A platform tool that would not say whether the entry exists leaves it exactly where it was,
        // and a silent zero here would read as "uninstalled" on a machine that still autostarts the
        // daemon. This is the one not-removed case that is a failure.
        if (removal.Warnings.Count > 0)
        {
            Console.Error.WriteLine(
                $"error: nothing was removed — the entry at {applier.ManagedTarget} could not be confirmed.");
            return 1;
        }

        // Nothing of ours to take away is not a failure: uninstall stays repeatable, but saying so
        // out loud on stderr is what keeps a silent exit code from reading as "it was removed".
        Console.Error.WriteLine(
            $"warning: no squeezr watchd autostart entry at {applier.ManagedTarget}; nothing was removed.");
        return 0;
    }
}
