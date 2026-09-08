using Spectre.Console;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

internal static class TuiExpandView
{
    public static async Task<string> RunAsync(
        IAnsiConsole console,
        string workspaceRoot,
        string repo,
        string file,
        int startLine,
        int? endLine,
        IReadOnlyList<string> exitActions,
        CancellationToken cancellationToken)
    {
        var contextLines = SpanExpander.DefaultContextLines;
        while (true)
        {
            ExpandResult expand;
            try
            {
                expand = new SpanExpander(workspaceRoot).Expand(
                    file, startLine, endLine, contextLines, repo, SpanExpander.DefaultMaxTokens);
            }
            catch (WorkspaceException ex)
            {
                console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
                return exitActions[0];
            }

            Render(console, expand);

            var choices = new List<string>(exitActions.Count + 1) { "More context (x2)" };
            choices.AddRange(exitActions);
            var action = await new SelectionPrompt<string>()
                .Title("Actions")
                .AddChoices(choices)
                .ShowAsync(console, cancellationToken);
            if (action == "More context (x2)")
            {
                contextLines *= 2;
                continue;
            }

            return action;
        }
    }

    public static void Render(IAnsiConsole console, ExpandResult expand)
    {
        console.MarkupLine(
            $"[bold]{expand.Repo.EscapeMarkup()}/{expand.File.EscapeMarkup()}[/]:{expand.StartLine}-{expand.EndLine} " +
            $"([grey]{expand.TokensUsed} tokens[/])");
        RenderLines(console, expand.Before, expand.StartLine - expand.Before.Count, "[grey]");
        RenderLines(console, expand.Span, expand.StartLine, "[bold]");
        RenderLines(console, expand.After, expand.EndLine + 1, "[grey]");
        if (expand.Truncated)
        {
            console.MarkupLine("[grey]context truncated by token budget[/]");
        }
    }

    private static void RenderLines(IAnsiConsole console, IReadOnlyList<string> lines, int firstLineNumber, string style)
    {
        var lineNumber = firstLineNumber;
        foreach (var line in lines)
        {
            console.MarkupLine($"{style}{lineNumber,5} │ {line.EscapeMarkup()}[/]");
            lineNumber++;
        }
    }
}
