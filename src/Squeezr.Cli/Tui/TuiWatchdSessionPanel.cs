using Spectre.Console;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Tui;

public static class TuiWatchdSessionPanel
{
    private const string Back = "Back";
    private const string StartAction = "Start watching";
    private const string StopAction = "Stop watching";
    private const string RestartAction = "Restart";
    private const int RestartPollLimit = 24;

    private static readonly TimeSpan RestartPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly string[] Actions = [StartAction, StopAction, RestartAction, Back];

    public static async Task RunAsync(
        IAnsiConsole console,
        WatchdSessionRow session,
        WatchdDaemonStatus daemon,
        SupervisionState state,
        IWatchdSupervisorSource source,
        CancellationToken cancellationToken)
    {
        RenderHeader(console, session, daemon);
        var action = await new SelectionPrompt<string>()
            .Title("Session actions")
            .PageSize(Actions.Length + 1)
            .AddChoices(Actions)
            .ShowAsync(console, cancellationToken);
        if (action == Back)
        {
            return;
        }

        await ApplyAsync(console, action, session.StoreKey, state, source, cancellationToken);
    }

    private static void RenderHeader(IAnsiConsole console, WatchdSessionRow session, WatchdDaemonStatus daemon)
    {
        var owner = session.OwnerPid is { } pid ? $", pid {pid}" : string.Empty;
        console.MarkupLine(
            $"[bold]{session.Name.EscapeMarkup()}[/] ([grey]{session.StoreKey.EscapeMarkup()}[/]){Markup.Escape(owner)}");
        console.MarkupLine(
            $"desired: {WatchdSessionText.Desire(session.Desire)}; state: {WatchdSessionText.State(session.State)}; " +
            $"health: {session.Health.EscapeMarkup()}");
        console.MarkupLine($"last pass: {session.LastPass.EscapeMarkup()}");
        console.MarkupLine($"root: {session.RootPath.EscapeMarkup()}");
        if (!daemon.Running)
        {
            console.MarkupLine("[yellow]watchd is not running — the wish is saved and applies on its start.[/]");
        }
    }

    private static async Task ApplyAsync(
        IAnsiConsole console,
        string action,
        string storeKey,
        SupervisionState state,
        IWatchdSupervisorSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case StartAction:
                    state.SetDesire(storeKey, WatchDesire.Running);
                    console.MarkupLine(
                        $"[green]desired state set to running for {Markup.Escape(storeKey)}[/].");
                    break;
                case StopAction:
                    state.SetDesire(storeKey, WatchDesire.Stopped);
                    console.MarkupLine(
                        $"[yellow]desired state set to stopped for {Markup.Escape(storeKey)}[/] — " +
                        "the supervisor stops the watcher on its next pass.");
                    break;
                case RestartAction:
                    await RestartAsync(console, storeKey, state, source, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[red]the wish could not be saved: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static async Task RestartAsync(
        IAnsiConsole console,
        string storeKey,
        SupervisionState state,
        IWatchdSupervisorSource source,
        CancellationToken cancellationToken)
    {
        state.SetDesire(storeKey, WatchDesire.Stopped);
        for (var attempt = 0; attempt < RestartPollLimit && source.IsWatcherRunning(storeKey); attempt++)
        {
            await Task.Delay(RestartPollInterval, cancellationToken);
        }

        state.SetDesire(storeKey, WatchDesire.Running);
        console.MarkupLine(
            $"[green]restart requested for {Markup.Escape(storeKey)}[/] — " +
            "the supervisor stops the watcher, then starts it fresh.");
    }
}
