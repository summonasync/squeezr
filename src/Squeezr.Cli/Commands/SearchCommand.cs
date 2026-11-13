using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

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
        [Description("Filter every search arm (exact, identifier, semantic) to paths matching the glob, e.g. \"src/**\"")]
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
        List<JsonWarningBody>? jsonWarnings = settings.Json ? [] : null;
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

            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: true, out var embeddings, out var vectorStore);
            foreach (var warning in warnings)
            {
                if (jsonWarnings is not null)
                {
                    jsonWarnings.Add(new JsonWarningBody(JsonWarningBody.EmbeddingsUnavailable, warning));
                }
                else
                {
                    ReportWarning(warning);
                }
            }

            var rankExclusion = RankExclusion.Load(workspaceRoot);
            foreach (var warning in rankExclusion.Warnings)
            {
                ReportWarning(warning);
            }

            var engine = new SearchEngine(workspaceRoot, rankExclusion);
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
                embeddings,
                vectorStore,
                cancellationToken).GetAwaiter().GetResult();

            var vectorStoreUnavailable = false;
            foreach (var warning in response.Warnings)
            {
                if (warning == SearchEngine.VectorStoreUnavailableWarning)
                {
                    vectorStoreUnavailable = true;
                    if (jsonWarnings is not null)
                    {
                        jsonWarnings.Add(new JsonWarningBody("vector_store_unavailable", warning));
                    }
                    continue;
                }

                ReportWarning(warning);
            }

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
                    response.LegUsage?.ExactTokens,
                    response.LegUsage?.SemanticTokens));
                if (jsonWarnings is { Count: > 0 })
                {
                    JsonOutput.WriteWarnings(jsonWarnings);
                }

                return 0;
            }

            foreach (var match in response.Matches)
            {
                var marker = match.Source == "exact"
                    ? string.Empty
                    : $" [grey][[{match.Source}]][/]";
                AnsiConsole.MarkupLine(
                    $"[bold]{(match.Repo + "/" + match.Path).EscapeMarkup()}[/]:{match.Line}: {match.Snippet.EscapeMarkup()}{marker}");
                if (match.CollapseNote() is { } note)
                {
                    AnsiConsole.MarkupLine($"  [grey]{note}[/]");
                }
            }

            if (response.Truncated && response.Hint is { } truncationHint)
            {
                var legs = response.LegUsage is { } usage
                    ? $"exact {usage.ExactTokens} tok, semantic {usage.SemanticTokens} tok"
                    : null;
                AnsiConsole.MarkupLine($"[grey]truncated: {truncationHint.ForCli()}{(legs is null ? string.Empty : $" ({legs})")}[/]");
            }

            if (vectorStoreUnavailable)
            {
                AnsiConsole.MarkupLine($"[grey]{SearchEngine.VectorStoreUnavailableWarning}[/]");
                var hint = QdrantEndpointHint.RestPortMisconfiguration(ConfigFile.Load(workspaceRoot).Qdrant.Endpoint);
                if (hint is not null)
                {
                    AnsiConsole.MarkupLine($"[grey]{hint.EscapeMarkup()}[/]");
                }
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

    private static void ReportWarning(string message) =>
        Console.Error.WriteLine($"warning: {message}");
}
