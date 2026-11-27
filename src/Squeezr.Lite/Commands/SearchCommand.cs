using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public sealed class SearchCommand : Command<SearchCommand.Settings>
{
    public sealed class Settings : WorkspaceTargetSettings
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
        [Description("Filter the search arms to paths matching the glob, e.g. \"src/**\"")]
        public string? PathGlob { get; set; }

        [CommandOption("--prefix")]
        public string? Prefix { get; set; }

        [CommandOption("--language")]
        public string? Language { get; set; }

        [CommandOption("--top-k")]
        public int? TopK { get; set; }

        [CommandOption("--max-tokens")]
        public int? MaxTokens { get; set; }

        [CommandOption("--kind")]
        public string? Kind { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (!DocKindDetector.TryNormalizeFilter(settings.Kind, out var kindFilter))
            {
                return CommandError.Report(
                    new WorkspaceException(
                        $"Unknown kind '{settings.Kind}' — allowed values: all, code, docs.",
                        WorkspaceException.InvalidQueryCode),
                    settings.Json);
            }

            var workspaceRoot = settings.RequireWorkspaceRoot();
            var sw = Stopwatch.StartNew();
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
                    settings.MaxTokens,
                    settings.Prefix,
                    kindFilter),
                semanticArm: null,
                cancellationToken).GetAwaiter().GetResult();

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "search",
                new { query = settings.Query, repo = settings.Repo, prefix = settings.Prefix, regex = settings.Regex, maxTokens = settings.MaxTokens, topK = settings.TopK, kind = kindFilter },
                response.TokensUsed, response.Truncated, response.Matches.Count,
                sw.ElapsedMilliseconds, indexAge: ResolveIndexAge(workspaceRoot), mapped: false);

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
                    response.Truncated,
                    response.Hint,
                    response.LegUsage?.ExactTokens));
                return 0;
            }

            foreach (var match in response.Matches)
            {
                AnsiConsole.MarkupLine(
                    $"[bold]{(match.Repo + "/" + match.Path).EscapeMarkup()}[/]:{match.Line}: {match.Snippet.EscapeMarkup()}");
                if (match.CollapseNote() is { } note)
                {
                    AnsiConsole.MarkupLine($"  [grey]{note}[/]");
                }
            }

            if (response.Truncated && response.Hint is { } truncationHint)
            {
                AnsiConsole.MarkupLine($"[grey]truncated: {truncationHint.ForCli()}[/]");
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or InvalidDataException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static TimeSpan? ResolveIndexAge(string workspaceRoot)
    {
        var lastIndexedAt = new WorkspaceManager(workspaceRoot).GetStatus().Repos
            .Select(r => r.LastIndexedAt)
            .Max();
        return lastIndexedAt is null ? null : DateTime.UtcNow - lastIndexedAt.Value.ToUniversalTime();
    }
}
