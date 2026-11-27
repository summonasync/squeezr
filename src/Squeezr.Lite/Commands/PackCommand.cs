using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public sealed class PackCommand : Command<PackCommand.Settings>
{
    private const int DefaultMaxTokens = 2000;

    public sealed class Settings : WorkspaceTargetSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; set; } = string.Empty;

        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--kind")]
        public string? Kind { get; set; }

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
            var budget = settings.MaxTokens ?? DefaultMaxTokens;
            var pack = new SymbolPackComposer(workspaceRoot).Compose(settings.Name, settings.Repo, settings.Kind, budget);

            var warnings = new List<string>();
            if (pack.File.Length == 0)
            {
                warnings.Add($"No symbol named '{settings.Name}' found.");
            }

            if (pack.WithheldCallers > 0)
            {
                warnings.Add(
                    $"{pack.WithheldCallers} caller(s) withheld to fit the budget — raise --max-tokens, or read the callers list");
            }

            QueryTelemetry.Record(workspaceRoot, QuerySession.Id, "pack",
                new { name = settings.Name, kind = settings.Kind, repo = settings.Repo, maxTokens = budget },
                pack.LookupTokens + pack.CallerTokens, !pack.Complete, pack.Callers.Count,
                sw.ElapsedMilliseconds, indexAge: SearchCommand.ResolveIndexAge(workspaceRoot),
                mapped: pack.Complete && pack.BodyOmitted);

            if (settings.Json)
            {
                JsonOutput.Write(PackJson.From(pack));
            }
            else
            {
                GuardOutput.WriteWithheld(pack.GuardWarnings.Count == 0 ? null : pack.GuardWarnings);
                Render(pack, settings.Name);
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void Render(SymbolPack pack, string requestedName)
    {
        if (pack.File.Length == 0)
        {
            if (pack.DidYouMean is { Count: > 0 })
            {
                AnsiConsole.MarkupLine($"[yellow]did you mean: {string.Join(", ", pack.DidYouMean.Select(s => s.EscapeMarkup()))}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]No symbol named '{requestedName.EscapeMarkup()}' in the index. Run '{LiteRemedy.IndexCommand}' if the code changed.[/]");
            }

            return;
        }

        AnsiConsole.MarkupLine($"[bold]{pack.Name.EscapeMarkup()}[/] ({pack.Kind}) " +
                               $"{pack.Repo.EscapeMarkup()}:{pack.File.EscapeMarkup()}:{pack.Span}");
        if (pack.ContainerName is not null)
        {
            AnsiConsole.MarkupLine($"  in {pack.ContainerName.EscapeMarkup()}");
        }

        if (pack.Signature is not null)
        {
            AnsiConsole.MarkupLine($"  {pack.Signature.EscapeMarkup()}");
        }

        if (pack.DocComment is not null)
        {
            AnsiConsole.MarkupLine($"  doc: {pack.DocComment.Replace("\n", " ").EscapeMarkup()}");
        }

        if (pack.BodyLines.Count > 0)
        {
            foreach (var line in pack.BodyLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
        else if (pack.BodyOmitted)
        {
            if (pack.Members.Count > 0)
            {
                AnsiConsole.MarkupLine("  body: omitted (budget) — members:");
                foreach (var member in pack.Members)
                {
                    AnsiConsole.MarkupLine(
                        $"    [grey]{member.Name.EscapeMarkup()} ({member.Kind.EscapeMarkup()}) :{member.Line}[/]");
                }

                if (pack.MembersOmitted > 0)
                {
                    AnsiConsole.MarkupLine($"    [grey]+{pack.MembersOmitted} more (budget)[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  body: omitted (budget); raise --max-tokens");
            }
        }

        AnsiConsole.MarkupLine($"  callers ({pack.Callers.Count + pack.WithheldCallers}):");
        foreach (var caller in pack.Callers)
        {
            var enclosing = caller.EnclosingSymbol is null ? string.Empty : $" (in {caller.EnclosingSymbol.EscapeMarkup()})";
            AnsiConsole.MarkupLine(
                $"    {caller.Repo.EscapeMarkup()}:{caller.File.EscapeMarkup()}:{caller.Line} {caller.Kind.EscapeMarkup()}{enclosing}");
        }

        foreach (var warning in WarningsOf(pack))
        {
            AnsiConsole.MarkupLine($"[grey]{warning.EscapeMarkup()}[/]");
        }

        MapCommand.WriteBudgetLine(
            pack.LookupTokens + pack.CallerTokens,
            !pack.Complete,
            pack.LookupTruncated ? pack.LookupHint?.ForCli() ?? HintText.RaiseMaxTokensRemedy : null);
    }

    private static IReadOnlyList<string> WarningsOf(SymbolPack pack)
    {
        var lines = new List<string>();
        if (pack.LookupTruncated)
        {
            lines.Add(HintText.TruncationLine(pack.LookupTokens, pack.BudgetTokens, HintText.RaiseMaxTokensRemedy));
        }

        if (pack.AdditionalMatches > 0)
        {
            lines.Add($"{pack.AdditionalMatches} same-name match(es) not shown — narrow with --repo or --kind");
        }

        return lines;
    }

    public sealed record PackJson(
        string Name,
        string Kind,
        string Repo,
        string File,
        string Span,
        string? Signature,
        string? ContainerName,
        string? DocComment,
        IReadOnlyList<string> BodyLines,
        bool BodyOmitted,
        IReadOnlyList<PackMemberJson> Members,
        int MembersOmitted,
        IReadOnlyList<PackCaller> Callers,
        int WithheldCallers,
        int LookupTokens,
        int CallerTokens,
        int BudgetTokens,
        bool LookupTruncated,
        int AdditionalMatches,
        bool Complete,
        IReadOnlyList<string>? GuardWarnings,
        IReadOnlyList<string>? DidYouMean,
        string State)
    {
        public static PackJson From(SymbolPack pack) => new(
            pack.Name,
            pack.Kind,
            pack.Repo,
            pack.File,
            pack.Span,
            pack.Signature,
            pack.ContainerName,
            pack.DocComment,
            pack.BodyLines,
            pack.BodyOmitted,
            [.. pack.Members.Select(m => new PackMemberJson(m.Name, m.Kind, m.Line))],
            pack.MembersOmitted,
            pack.Callers,
            pack.WithheldCallers,
            pack.LookupTokens,
            pack.CallerTokens,
            pack.BudgetTokens,
            pack.LookupTruncated,
            pack.AdditionalMatches,
            pack.Complete,
            pack.GuardWarnings.Count == 0 ? null : pack.GuardWarnings,
            pack.DidYouMean,
            pack.State.StateName());
    }

    public sealed record PackMemberJson(string Name, string Kind, int Line);
}
