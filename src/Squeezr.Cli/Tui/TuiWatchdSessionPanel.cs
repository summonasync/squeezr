using Spectre.Console;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Tui;

public static class TuiWatchdSessionPanel
{
    private const string Back = "Back";
    internal const string StartAction = "Start watching";
    internal const string StopAction = "Stop watching";
    internal const string RestartAction = "Restart";
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

        await ApplyAsync(console, action, session, state, source, cancellationToken);
    }

    private static void RenderHeader(IAnsiConsole console, WatchdSessionRow session, WatchdDaemonStatus daemon)
    {
        var owner = session.OwnerPid is { } pid ? $", pid {pid}" : string.Empty;
        console.MarkupLine(
            $"[bold]{session.Name.EscapeMarkup()}[/] ([grey]{session.StoreKey.EscapeMarkup()}[/]){Markup.Escape(owner)}");
        var desire = session.DesireKnown ? WatchdSessionText.Desire(session.Desire) : "unknown";
        console.MarkupLine(
            $"desired: {desire}; state: {WatchdSessionText.State(session.State)}; " +
            $"health: {session.Health.EscapeMarkup()}");
        console.MarkupLine($"last pass: {session.LastPass.EscapeMarkup()}");
        console.MarkupLine($"root: {session.RootPath.EscapeMarkup()}");
        if (!daemon.Running)
        {
            console.MarkupLine("[yellow]watchd is not running — the wish is saved and applies on its start.[/]");
        }
    }

    internal static async Task ApplyAsync(
        IAnsiConsole console,
        string action,
        WatchdSessionRow session,
        SupervisionState state,
        IWatchdSupervisorSource source,
        CancellationToken cancellationToken)
    {
        // A wish for a store this machine cannot reach is a line in a file no supervisor will ever read: it
        // outlives the workspace it names and makes the next status row look wanted. Refuse and write nothing.
        if (!session.RootResolves)
        {
            console.MarkupLine(
                $"[red]wish not saved[/]: '{Markup.Escape(session.StoreKey)}' does not resolve on this machine " +
                $"(root '{session.RootPath.EscapeMarkup()}' is gone).");
            console.MarkupLine(
                $"[grey]remove the stale entry with 'squeezr drop --store-key {Markup.Escape(session.StoreKey)}'.[/]");
            return;
        }

        try
        {
            switch (action)
            {
                case StartAction:
                    state.SetDesire(session.StoreKey, WatchDesire.Running);
                    console.MarkupLine(
                        $"[green]desired state set to running for {Markup.Escape(session.StoreKey)}[/].");
                    break;
                case StopAction:
                    state.SetDesire(session.StoreKey, WatchDesire.Stopped);
                    console.MarkupLine(
                        $"[yellow]desired state set to stopped for {Markup.Escape(session.StoreKey)}[/] — " +
                        "the supervisor stops the watcher on its next pass.");
                    break;
                case RestartAction:
                    await RestartAsync(
                        console, session.StoreKey, state, source, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[red]the wish could not be saved: {Markup.Escape(ex.Message)}[/]");
        }
    }

    internal static async Task RestartAsync(
        IAnsiConsole console,
        string storeKey,
        SupervisionState state,
        IWatchdSupervisorSource source,
        CancellationToken cancellationToken,
        int pollLimit = RestartPollLimit,
        TimeSpan? pollInterval = null)
    {
        var gap = pollInterval ?? RestartPollInterval;
        state.SetDesire(storeKey, WatchDesire.Stopped);
        var watcherSeen = source.IsWatcherRunning(storeKey);
        var watcher = watcherSeen;
        for (var attempt = 0; watcher && attempt < pollLimit; attempt++)
        {
            await Task.Delay(gap, cancellationToken);
            watcher = source.IsWatcherRunning(storeKey);
        }

        state.SetDesire(storeKey, WatchDesire.Running);
        if (!watcherSeen)
        {
            console.MarkupLine(
                $"[yellow]no watcher was running for {Markup.Escape(storeKey)}[/] — the desire is back to " +
                "running and the supervisor starts it on its next pass.");
            return;
        }

        if (watcher)
        {
            console.MarkupLine(
                $"[yellow]restart pending for {Markup.Escape(storeKey)}[/] — the watcher was still up when " +
                "the panel stopped asking; the supervisor will converge on its own passes.");
            return;
        }

        console.MarkupLine(
            $"[green]restart requested for {Markup.Escape(storeKey)}[/] — " +
            "the supervisor stops the watcher, then starts it fresh.");
    }
}
