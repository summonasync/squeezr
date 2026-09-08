using Spectre.Console;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiSearchFlow
{
    private const int SearchMaxTokens = 2000;
    private const int HitListSize = 20;

    public static async Task RunAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            console.MarkupLine("[yellow]Workspace not initialized.[/] Run 'init' from the Wizards menu first.");
            return;
        }

        IEmbeddingProvider? embeddings = null;
        IVectorStore? vectorStore = null;
        var runtimeResolved = false;

        while (true)
        {
            var query = await new TextPrompt<string>("Search query (empty to go back)")
                .AllowEmpty()
                .ShowAsync(console, cancellationToken);
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            if (!runtimeResolved)
            {
                runtimeResolved = true;
                foreach (var warning in EmbeddingRuntimeFactory.TryCreate(
                    workspaceRoot, withProvider: true, out embeddings, out vectorStore))
                {
                    console.MarkupLine($"[grey]{warning.EscapeMarkup()}[/]");
                }
            }

            SearchResponse response;
            try
            {
                response = await new SearchEngine(workspaceRoot).SearchHybridAsync(
                    new SearchQuery(
                        query.Trim(),
                        Regex: false,
                        IgnoreCase: null,
                        Repo: null,
                        PathGlob: null,
                        Language: null,
                        TopK: HitListSize,
                        MaxTokens: SearchMaxTokens),
                    embeddings,
                    vectorStore,
                    cancellationToken);
            }
            catch (WorkspaceException ex)
            {
                console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
                return;
            }

            foreach (var warning in response.Warnings)
            {
                console.MarkupLine($"[yellow]warning:[/] {warning.EscapeMarkup()}");
            }

            if (response.Matches.Count == 0)
            {
                console.MarkupLine("[grey]No hits.[/] Try a different query.");
                continue;
            }

            if (!await ShowHitsAsync(console, workspaceRoot, query.Trim(), response, cancellationToken))
            {
                return;
            }
        }
    }

    private static async Task<bool> ShowHitsAsync(
        IAnsiConsole console,
        string workspaceRoot,
        string query,
        SearchResponse response,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var items = response.Matches.Select(HitLabel).ToList();
            items.Add("New query");
            items.Add("Back to menu");

            var truncatedNote = response.Truncated ? " [grey](truncated — refine the query)[/]" : string.Empty;
            var choice = await new SelectionPrompt<string>()
                .Title($"Hits for '{query.EscapeMarkup()}'{truncatedNote}")
                .PageSize(15)
                .AddChoices(items)
                .ShowAsync(console, cancellationToken);

            var index = items.IndexOf(choice);
            if (index >= response.Matches.Count)
            {
                return index == response.Matches.Count;
            }

            var action = await ExpandHitAsync(console, workspaceRoot, response.Matches[index], cancellationToken);
            if (action == HitAction.NewQuery)
            {
                return true;
            }

            if (action == HitAction.MainMenu)
            {
                return false;
            }
        }
    }

    private static async Task<HitAction> ExpandHitAsync(
        IAnsiConsole console,
        string workspaceRoot,
        SearchMatch match,
        CancellationToken cancellationToken)
    {
        var action = await TuiExpandView.RunAsync(
            console,
            workspaceRoot,
            match.Repo,
            match.Path,
            match.Line,
            endLine: null,
            ["Back to hits", "New query", "Main menu"],
            cancellationToken);
        return action switch
        {
            "New query" => HitAction.NewQuery,
            "Main menu" => HitAction.MainMenu,
            _ => HitAction.BackToHits,
        };
    }

    private static string HitLabel(SearchMatch match) =>
        $"[{match.Source}] {match.Repo}/{match.Path}:{match.Line}  {match.Snippet}".EscapeMarkup();

    private enum HitAction
    {
        BackToHits,
        NewQuery,
        MainMenu,
    }
}
