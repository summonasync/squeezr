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
    public sealed class Settings : WorkspaceTargetSettings
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
            var workspaceRoot = settings.RequireWorkspaceRoot();
            var sw = Stopwatch.StartNew();
            var result = new RefLookup(workspaceRoot).Find(settings.Symbol, settings.Repo);
            // repo belongs in the key: two repos over the same relative layout (a checkout plus its
            // worktree) hold different callsites that render identical paths
            var refs = result.Groups
                .SelectMany(g => g.Refs, (g, r) => new { Repo = g.Repo, r.File, r.Line, r.Kind, r.EnclosingSymbol })
                .DistinctBy(x => (x.Repo, x.File, x.Line, x.Kind, x.EnclosingSymbol))
                .ToList();

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "refs",
                new { symbol = settings.Symbol, repo = settings.Repo },
                refs.Sum(r => TokenCounter.Estimate($"{r.File}:{r.Line} {r.Kind}")), false, refs.Count,
                sw.ElapsedMilliseconds, indexAge: SearchCommand.ResolveIndexAge(workspaceRoot));

            if (settings.Json)
            {
                JsonOutput.Write(new
                {
                    symbol = result.Symbol,
                    definitions = result.Groups
                        .SelectMany(g => g.Definitions, (g, d) => new { Repo = g.Repo, d.Kind, d.File, d.StartLine, d.EndLine })
                        .ToList(),
                    refs
                });
            }
            else
            {
                GuardOutput.WriteWithheld(result.GuardWarnings);
                foreach (var group in result.Groups)
                {
                    RenderHeader(group);
                    if (group.Refs.Count == 0)
                    {
                        AnsiConsole.MarkupLine("  no references");
                        continue;
                    }

                    foreach (var hit in group.Refs)
                    {
                        var enclosing = hit.EnclosingSymbol is null ? string.Empty : $" (in {hit.EnclosingSymbol.EscapeMarkup()})";
                        AnsiConsole.MarkupLine($"  {hit.File.EscapeMarkup()}:{hit.Line} {hit.Kind.EscapeMarkup()}{enclosing}");
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

    private static void RenderHeader(SymbolRefGroup group)
    {
        if (group.Definitions.Count == 1)
        {
            var sole = group.Definitions[0];
            AnsiConsole.MarkupLine($"[bold]{group.Name.EscapeMarkup()}[/] ({sole.Kind}) " +
                                   $"{group.Repo.EscapeMarkup()}:{sole.File.EscapeMarkup()}:{sole.StartLine}-{sole.EndLine}");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]{group.Name.EscapeMarkup()}[/] ({group.Definitions.Count} definitions, " +
                               $"{group.Refs.Count} references)");
        foreach (var definition in group.Definitions)
        {
            AnsiConsole.MarkupLine($"  def {definition.Kind.EscapeMarkup()} " +
                                   $"{group.Repo.EscapeMarkup()}:{definition.File.EscapeMarkup()}:{definition.StartLine}-{definition.EndLine}");
        }
    }
}
