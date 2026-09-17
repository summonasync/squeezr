using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class RefsCommand : Command<RefsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<symbol>")]
        public string Symbol { get; set; } = string.Empty;

        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            var sw = Stopwatch.StartNew();
            var result = new RefLookup(workspaceRoot).Find(settings.Symbol, settings.Repo);
            var refs = result.Groups.SelectMany(g => g.Refs).ToList();

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "refs",
                new { symbol = settings.Symbol, repo = settings.Repo },
                refs.Sum(r => TokenCounter.Estimate($"{r.File}:{r.Line} {r.Kind}")), false, refs.Count,
                sw.ElapsedMilliseconds, indexAge: SearchCommand.ResolveIndexAge(workspaceRoot));

            if (settings.Json)
            {
                JsonOutput.Write(new
                {
                    symbol = result.Symbol,
                    refs
                });
            }
            else
            {
                foreach (var group in result.Groups)
                {
                    AnsiConsole.MarkupLine($"[bold]{group.Name.EscapeMarkup()}[/] ({group.Kind}) " +
                                           $"{group.Repo.EscapeMarkup()}:{group.File.EscapeMarkup()}:{group.StartLine}-{group.EndLine}");
                    if (group.Refs.Count == 0)
                    {
                        AnsiConsole.MarkupLine("  no references");
                        continue;
                    }

                    foreach (var fileGroup in group.Refs.GroupBy(r => r.File))
                    {
                        foreach (var hit in fileGroup)
                        {
                            var enclosing = hit.EnclosingSymbol is null ? string.Empty : $" (in {hit.EnclosingSymbol.EscapeMarkup()})";
                            AnsiConsole.MarkupLine($"  {hit.File.EscapeMarkup()}:{hit.Line} {hit.Kind.EscapeMarkup()}{enclosing}");
                        }
                    }
                }
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }
}
