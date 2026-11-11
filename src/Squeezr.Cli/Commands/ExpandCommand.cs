using System.Diagnostics;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class ExpandCommand : Command<ExpandCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        public string File { get; set; } = string.Empty;

        [CommandArgument(1, "<startLine>")]
        public int StartLine { get; set; }

        [CommandArgument(2, "[endLine]")]
        public int? EndLine { get; set; }

        [CommandOption("--context")]
        public int? Context { get; set; }

        [CommandOption("--repo")]
        public string? Repo { get; set; }

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
            var sw = Stopwatch.StartNew();
            var result = new SpanExpander(workspaceRoot).Expand(
                settings.File,
                settings.StartLine,
                settings.EndLine,
                settings.Context ?? SpanExpander.DefaultContextLines,
                settings.Repo,
                settings.MaxTokens ?? SpanExpander.DefaultMaxTokens);

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "expand",
                new { file = settings.File, startLine = settings.StartLine, endLine = settings.EndLine, context = settings.Context },
                result.TokensUsed, result.Truncated, result.Span.Count,
                sw.ElapsedMilliseconds, indexAge: SearchCommand.ResolveIndexAge(workspaceRoot), mapped: false);

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else
            {
                var lineNo = result.StartLine - result.Before.Count;
                foreach (var line in result.Before)
                {
                    Console.WriteLine($"{lineNo++,4}  {line}");
                }

                foreach (var line in result.Span)
                {
                    Console.WriteLine($"{lineNo++,4}> {line}");
                }

                foreach (var line in result.After)
                {
                    Console.WriteLine($"{lineNo++,4}  {line}");
                }

                Console.WriteLine($"tokens: {result.TokensUsed}");
                if (result.Truncated)
                {
                    Console.WriteLine("truncated: output cut at --max-tokens");
                }
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json, ex.ForCli());
        }
    }
}
