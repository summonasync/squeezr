using Spectre.Console;
using Spectre.Console.Cli;
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
            var engine = new SearchEngine(Directory.GetCurrentDirectory());
            var response = engine.Search(new SearchQuery(
                settings.Query,
                settings.Regex,
                settings.IgnoreCase ? false : null,
                settings.Repo,
                settings.PathGlob,
                settings.Language,
                settings.TopK,
                settings.MaxTokens));

            if (settings.Json)
            {
                JsonOutput.Write(response);
                return 0;
            }

            foreach (var match in response.Matches)
            {
                AnsiConsole.MarkupLine(
                    $"[bold]{(match.Repo + "/" + match.Path).EscapeMarkup()}[/]:{match.Line}: {match.Snippet.EscapeMarkup()}");
            }

            if (response.Truncated)
            {
                AnsiConsole.MarkupLine("[grey]truncated: refine the query or raise --max-tokens[/]");
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (settings.Json)
            {
                JsonOutput.Write(new { Error = ex.Message });
            }

            return 1;
        }
    }
}
