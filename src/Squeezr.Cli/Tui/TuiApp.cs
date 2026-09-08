using Spectre.Console;

namespace Squeezr.Cli.Tui;

public static class TuiApp
{
    private static readonly string[] MenuItems = ["Dashboard", "Index", "Search", "Map", "Config", "Wizards", "Exit"];

    public static async Task<int> RunInteractiveAsync(string workspaceRoot)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(AnsiConsole.Console, workspaceRoot, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    public static async Task<int> RunAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string choice;
            try
            {
                choice = await new SelectionPrompt<string>()
                    .Title("Squeezr")
                    .PageSize(10)
                    .AddChoices(MenuItems)
                    .ShowAsync(console, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            try
            {
                switch (choice)
                {
                    case "Dashboard":
                        await TuiDashboard.RenderAsync(console, workspaceRoot, cancellationToken);
                        break;
                    case "Index":
                        await TuiIndexRunner.RunAsync(console, workspaceRoot, cancellationToken);
                        break;
                    case "Search":
                        await TuiSearchFlow.RunAsync(console, workspaceRoot, cancellationToken);
                        break;
                    case "Map":
                        await TuiMapBrowser.RunAsync(console, workspaceRoot, cancellationToken);
                        break;
                    case "Config":
                        await TuiConfigEditor.RunAsync(console, workspaceRoot, cancellationToken);
                        break;
                    case "Wizards":
                        await RunWizardAsync(console, workspaceRoot, cancellationToken);
                        break;
                    case "Exit":
                        return 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
        }

        return 0;
    }

    internal static async Task RunWizardAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        var wizard = await new SelectionPrompt<string>()
            .Title("Wizards")
            .AddChoices("init", "add", "drop", "Back")
            .ShowAsync(console, cancellationToken);

        switch (wizard)
        {
            case "init":
                await TuiWizards.InitAsync(console, workspaceRoot, cancellationToken);
                break;
            case "add":
                await TuiWizards.AddAsync(console, workspaceRoot, cancellationToken);
                break;
            case "drop":
                await TuiWizards.DropAsync(console, workspaceRoot, cancellationToken);
                break;
        }
    }
}
