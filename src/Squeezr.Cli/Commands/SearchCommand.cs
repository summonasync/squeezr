using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class SearchCommand : Command<SearchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<query>")]
        public string Query { get; set; } = string.Empty;

        [CommandOption("--regex")]
        public bool Regex { get; set; }

        [CommandOption("-i|--ignore-case")]
        public bool IgnoreCase { get; set; }

        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--path-glob")]
        public string? PathGlob { get; set; }

        [CommandOption("--language")]
        public string? Language { get; set; }

        [CommandOption("--top-k")]
        public int? TopK { get; set; }

        [CommandOption("--max-tokens")]
        public int? MaxTokens { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();

            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: true, out var embeddings, out var vectorStore);
            foreach (var warning in warnings)
            {
                ReportWarning(warning);
            }

            var engine = new SearchEngine(workspaceRoot);
            var response = engine.SearchHybridAsync(
                new SearchQuery(
                    settings.Query,
                    settings.Regex,
                    settings.IgnoreCase ? false : null,
                    settings.Repo,
                    settings.PathGlob,
                    settings.Language,
                    settings.TopK,
                    settings.MaxTokens),
                embeddings,
                vectorStore,
                cancellationToken).GetAwaiter().GetResult();

            var vectorStoreUnavailable = false;
            foreach (var warning in response.Warnings)
            {
                if (warning == SearchEngine.VectorStoreUnavailableWarning)
                {
                    vectorStoreUnavailable = true;
                    continue;
                }

                ReportWarning(warning);
            }

            if (settings.Json)
            {
                foreach (var match in response.Matches)
                {
                    JsonOutput.WriteLine(SearchMatchLine.FromMatch(match));
                }

                JsonOutput.WriteLine(new SearchSummaryLine(
                    "summary",
                    response.Matches.Count,
                    response.TokensUsed,
                    settings.MaxTokens,
                    response.Truncated));
                return 0;
            }

            foreach (var match in response.Matches)
            {
                var marker = match.Source == "exact"
                    ? string.Empty
                    : $" [grey][[{match.Source}]][/]";
                AnsiConsole.MarkupLine(
                    $"[bold]{(match.Repo + "/" + match.Path).EscapeMarkup()}[/]:{match.Line}: {match.Snippet.EscapeMarkup()}{marker}");
            }

            if (response.Truncated)
            {
                AnsiConsole.MarkupLine("[grey]truncated: refine the query or raise --max-tokens[/]");
            }

            if (vectorStoreUnavailable)
            {
                AnsiConsole.MarkupLine($"[grey]{SearchEngine.VectorStoreUnavailableWarning}[/]");
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or InvalidDataException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void ReportWarning(string message) =>
        Console.Error.WriteLine($"warning: {message}");
}
