using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public sealed class MapCommand : Command<MapCommand.Settings>
{
    public sealed class Settings : WorkspaceTargetSettings
    {
        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--prefix")]
        public string? Prefix { get; set; }

        [CommandOption("--depth")]
        public int? Depth { get; set; }

        [CommandOption("--max-tokens")]
        public int? MaxTokens { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = settings.RequireWorkspaceRoot();
            var sw = Stopwatch.StartNew();
            var result = new RepoMapper(workspaceRoot).Build(
                settings.Repo,
                settings.Depth ?? RepoMapper.DefaultDepth,
                settings.MaxTokens ?? RepoMapper.DefaultMaxTokens,
                settings.Prefix);

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "map",
                new { repo = settings.Repo, prefix = settings.Prefix, depth = settings.Depth, maxTokens = settings.MaxTokens },
                result.TokensUsed, result.Truncated, result.Lines.Count,
                sw.ElapsedMilliseconds, indexAge: SearchCommand.ResolveIndexAge(workspaceRoot), mapped: false);

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else
            {
                GuardOutput.WriteWithheld(result.GuardWarnings);
                foreach (var line in result.Lines)
                {
                    Console.WriteLine($"{new string(' ', line.Depth * 2)}{line.Text}");
                }

                WriteBudgetLine(
                    result.TokensUsed,
                    result.Truncated,
                    result.Truncated ? result.Hint?.ForCli() : null);
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static void WriteBudgetLine(int tokensUsed, bool truncated, string? remedy)
    {
        if (!truncated)
        {
            AnsiConsole.MarkupLine($"[grey]tokens: {tokensUsed}[/]");
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(remedy) ? string.Empty : $" — {remedy}";
        AnsiConsole.MarkupLine($"[grey]tokens: {tokensUsed}, truncated{suffix}[/]");
    }
}
