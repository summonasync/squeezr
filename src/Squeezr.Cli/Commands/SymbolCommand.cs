using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class SymbolCommand : Command<SymbolCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; set; } = string.Empty;

        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--kind")]
        public string? Kind { get; set; }

        [CommandOption("--max-tokens")]
        public int? MaxTokens { get; set; }

        [CommandOption("--body")]
        public bool Body { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            var sw = Stopwatch.StartNew();
            var result = new SymbolLookup(workspaceRoot).Find(
                settings.Name,
                settings.Repo,
                settings.Kind,
                settings.MaxTokens ?? SymbolLookup.DefaultMaxTokens,
                settings.Body);

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "symbol",
                new { name = settings.Name, kind = settings.Kind, repo = settings.Repo },
                result.TokensUsed, result.Truncated, result.Matches.Count,
                sw.ElapsedMilliseconds, indexAge: SearchCommand.ResolveIndexAge(workspaceRoot));

            if (settings.Json)
            {
                JsonOutput.Write(SymbolLookupJson.From(result));
            }
            else
            {
                GuardOutput.WriteWithheld(result.GuardWarnings);
                foreach (var hit in result.Matches)
                {
                    AnsiConsole.MarkupLine($"[bold]{hit.Name.EscapeMarkup()}[/] ({hit.Kind}) " +
                                           $"{hit.Repo.EscapeMarkup()}:{hit.File.EscapeMarkup()}:{hit.StartLine}-{hit.EndLine}");
                    if (hit.ContainerName is not null)
                    {
                        AnsiConsole.MarkupLine($"  in {hit.ContainerName.EscapeMarkup()}");
                    }

                    if (hit.Signature is not null)
                    {
                        AnsiConsole.MarkupLine($"  {hit.Signature.EscapeMarkup()}");
                    }

                    if (hit.DocComment is not null)
                    {
                        AnsiConsole.MarkupLine($"  doc: {hit.DocComment.Replace("\n", " ").EscapeMarkup()}");
                    }

                    if (hit.Body is not null)
                    {
                        AnsiConsole.MarkupLine($"  [grey]{hit.Body.EscapeMarkup()}[/]");
                    }
                    else if (hit.Members.Count > 0)
                    {
                        AnsiConsole.MarkupLine("  body: omitted (budget) — members:");
                        foreach (var member in hit.Members)
                        {
                            AnsiConsole.MarkupLine(
                                $"    [grey]{member.Name.EscapeMarkup()} ({member.Kind.EscapeMarkup()}) :{member.Line}[/]");
                        }

                        if (hit.MembersOmitted > 0)
                        {
                            AnsiConsole.MarkupLine($"    [grey]+{hit.MembersOmitted} more (budget)[/]");
                        }
                    }
                    else if (hit.BodyOmitted)
                    {
                        AnsiConsole.MarkupLine("  body: omitted (budget); use --body or raise --max-tokens");
                    }
                }

                if (result.Matches.Count == 0)
                {
                    if (result.DidYouMean is not null && result.DidYouMean.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[yellow]did you mean: {string.Join(", ", result.DidYouMean.Select(s => s.EscapeMarkup()))}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]No symbol named '{settings.Name.EscapeMarkup()}' in the index. Run 'squeezr index' if the code changed.[/]");
                        AnsiConsole.MarkupLine("[yellow]use code_search for paraphrase queries[/]");
                    }
                }

                WriteBudgetLine(
                    result.TokensUsed,
                    result.State,
                    result.State is AnswerState.Clipped ? result.RemedyForCli() : null);
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static void WriteBudgetLine(int tokensUsed, AnswerState state, string? remedy)
    {
        if (state is not AnswerState.Clipped)
        {
            AnsiConsole.MarkupLine($"[grey]tokens: {tokensUsed}[/]");
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(remedy) ? string.Empty : $" — {remedy}";
        AnsiConsole.MarkupLine($"[grey]tokens: {tokensUsed}, truncated{suffix}[/]");
    }
}
