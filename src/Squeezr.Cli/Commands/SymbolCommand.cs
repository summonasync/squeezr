using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Navigation;
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
            var result = new SymbolLookup(Directory.GetCurrentDirectory()).Find(
                settings.Name,
                settings.Repo,
                settings.Kind,
                settings.MaxTokens ?? SymbolLookup.DefaultMaxTokens,
                settings.Body);

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else
            {
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
                    else if (hit.BodyOmitted)
                    {
                        AnsiConsole.MarkupLine("  body: omitted (budget); use --body or raise --max-tokens");
                    }
                }

                WriteBudgetLine(result.TokensUsed, result.Truncated, result.Hint);
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static void WriteBudgetLine(int tokensUsed, bool truncated, string? hint)
    {
        var suffix = truncated && hint is not null ? $" — {hint}" : string.Empty;
        AnsiConsole.MarkupLine($"[grey]tokens: {tokensUsed}{(truncated ? ", truncated" : string.Empty)}{suffix}[/]");
    }
}
