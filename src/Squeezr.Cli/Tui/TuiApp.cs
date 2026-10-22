using Spectre.Console;

namespace Squeezr.Cli.Tui;

public static class TuiApp
{
    private static readonly string[] MainMenuItems =
    [
        "Dashboard",
        "Search",
        "Map",
        "Index",
        "Add repository",
        "Config",
        "Drop workspace",
        "Token stats",
        "Usage analytics",
        "Exit",
    ];

    private static readonly string[] UninitializedMenuItems = ["Initialize workspace", "Exit"];

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
        var session = new TuiSession();
        try
        {
            await OfferInitializationOnEntryAsync(console, workspaceRoot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var state = TuiWorkspaceStateDetector.Detect(workspaceRoot);
            string choice;
            try
            {
                choice = await PromptMenuAsync(console, state, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            if (choice == "Exit")
            {
                return 0;
            }

            try
            {
                await ExecuteAsync(console, workspaceRoot, choice, session, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                continue;
            }
        }

        return 0;
    }

    private static async Task OfferInitializationOnEntryAsync(
        IAnsiConsole console,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (TuiWorkspaceStateDetector.Detect(workspaceRoot) != TuiWorkspaceState.Uninitialized)
        {
            return;
        }

        var accepted = await new ConfirmationPrompt("No workspace found here. Initialize now?")
        {
            DefaultValue = true,
        }
            .ShowAsync(console, cancellationToken);
        if (accepted)
        {
            await TuiWizards.InitAsync(console, workspaceRoot, cancellationToken);
        }
    }

    private static async Task<string> PromptMenuAsync(
        IAnsiConsole console,
        TuiWorkspaceState state,
        CancellationToken cancellationToken)
    {
        var items = state == TuiWorkspaceState.Uninitialized ? UninitializedMenuItems : MainMenuItems;
        return await new SelectionPrompt<string>()
            .Title("Squeezr")
            .PageSize(10)
            .AddChoices(items)
            .ShowAsync(console, cancellationToken);
    }

    private static async Task ExecuteAsync(
        IAnsiConsole console,
        string workspaceRoot,
        string choice,
        TuiSession session,
        CancellationToken cancellationToken)
    {
        switch (choice)
        {
            case "Dashboard":
                await TuiDashboard.RenderAsync(console, workspaceRoot, cancellationToken);
                if (TuiWorkspaceStateDetector.Detect(workspaceRoot) == TuiWorkspaceState.NoRepositories)
                {
                    await OfferAddRepositoryAsync(console, workspaceRoot, cancellationToken);
                }

                break;
            case "Search":
                await TuiSearchFlow.RunAsync(console, workspaceRoot, session, cancellationToken);
                break;
            case "Map":
                await TuiMapBrowser.RunAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Index":
                await TuiIndexRunner.RunAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Initialize workspace":
                await TuiWizards.InitAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Add repository":
                await TuiWizards.AddAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Config":
                await TuiConfigEditor.RunAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Drop workspace":
                await TuiWizards.DropAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Token stats":
                await TuiStatsView.RenderAsync(console, workspaceRoot, cancellationToken);
                break;
            case "Usage analytics":
                await TuiUsageView.RenderAsync(console, workspaceRoot, cancellationToken);
                break;
        }
    }

    private static async Task OfferAddRepositoryAsync(
        IAnsiConsole console,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var accepted = await new ConfirmationPrompt("No repositories yet. Add one now?")
        {
            DefaultValue = true,
        }
            .ShowAsync(console, cancellationToken);
        if (accepted)
        {
            await TuiWizards.AddAsync(console, workspaceRoot, cancellationToken);
        }
    }
}
