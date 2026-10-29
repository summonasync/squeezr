using Spectre.Console;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Tui;

public static class TuiWatchdSupervisor
{
    private const string Back = "Back";

    public static async Task RunAsync(IAnsiConsole console, CancellationToken cancellationToken = default) =>
        await RunAsync(console, new WatchdSupervisorSource(), new SupervisionState(), cancellationToken);

    public static async Task RunAsync(
        IAnsiConsole console,
        IWatchdSupervisorSource source,
        SupervisionState state,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var view = source.Read();
            Render(console, view);
            var selected = await PromptSessionAsync(console, view, cancellationToken);
            if (selected is null)
            {
                return;
            }

            await TuiWatchdSessionPanel.RunAsync(console, selected, view.Daemon, state, source, cancellationToken);
        }
    }

    private static void Render(IAnsiConsole console, WatchdSupervisorView view)
    {
        if (view.Sessions.Count == 0)
        {
            console.MarkupLine("No stores registered yet — add a repository first.");
            return;
        }

        var table = new Table().Expand().BorderColor(Color.Grey);
        table.AddColumn("workspace");
        table.AddColumn("store key");
        table.AddColumn("desire");
        table.AddColumn("state");
        table.AddColumn("health");
        table.AddColumn("last pass");
        foreach (var session in view.Sessions)
        {
            table.AddRow(
                Markup.Escape(session.Name),
                Markup.Escape(session.StoreKey),
                DesireCell(session.Desire),
                StateCell(session.State),
                Markup.Escape(session.Health),
                Markup.Escape(session.LastPass));
        }

        console.Write(table);
        if (!view.Daemon.Running)
        {
            console.MarkupLine("[yellow]watchd is not running — rows are the last known state.[/]");
            console.MarkupLine("[yellow]Wishes set here are saved and apply when it starts.[/]");
        }
    }

    private static string DesireCell(WatchDesire desire) => desire == WatchDesire.Running
        ? "[green]running[/]"
        : "[grey]stopped[/]";

    private static string StateCell(WatchdSessionState state) => state switch
    {
        WatchdSessionState.Running => "[green]running[/]",
        WatchdSessionState.Stopped => "[grey]stopped[/]",
        WatchdSessionState.Wedged => "[red]wedged[/]",
        _ => "[yellow]managed elsewhere[/]",
    };

    private static async Task<WatchdSessionRow?> PromptSessionAsync(
        IAnsiConsole console,
        WatchdSupervisorView view,
        CancellationToken cancellationToken)
    {
        var byDisplay = new Dictionary<string, WatchdSessionRow>(StringComparer.Ordinal);
        var choices = new List<string>();
        foreach (var session in view.Sessions)
        {
            var display = $"{session.Name} — {WatchdSessionText.State(session.State)}";
            byDisplay[display] = session;
            choices.Add(display);
        }

        choices.Add(Back);
        var choice = await new SelectionPrompt<string>()
            .Title("Watchd supervisor — sessions")
            .PageSize(Math.Max(choices.Count + 1, 3))
            .AddChoices(choices)
            .ShowAsync(console, cancellationToken);
        return choice == Back ? null : byDisplay[choice];
    }
}
