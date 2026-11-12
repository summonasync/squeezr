using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorktreeDropCommand : AsyncCommand<WorktreeDropCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Worktree root to drop; defaults to the current directory")]
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(settings.Path)
                ? WorkspaceRootResolver.LaunchDirectory()
                : settings.Path!;

            var plan = WorkspaceRootResolver.GuardRegistryIo(() => WorktreeDrop.Plan(target));
            var outcome = await WorktreeFolder.RunAsync(plan.Claims, cancellationToken);
            var stores = outcome.Stores;
            var halted = outcome.Halted;

            var report = new WorktreeDropReport(
                plan.RootPath, plan.Branch, plan.ParentRootPath, plan.IsLinked, plan.DependentLanes,
                stores, outcome.NotAttempted);

            if (plan.DependentLanes > 0)
            {
                Console.Error.WriteLine(
                    $"warning: {plan.DependentLanes} worktree row(s) name '{plan.RootPath}' as their parent; their " +
                    $"stores were left alone, and reap them when their own trees go: {RemedyText.WorkspaceRepairCommand}");
            }

            if (settings.Json)
            {
                JsonOutput.Write(report);
                return halted is null ? 0 : CommandError.Report(halted, settings.Json);
            }

            WriteHuman(report, halted);
            return halted is null ? 0 : CommandError.Report(halted, settings.Json);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void WriteHuman(WorktreeDropReport report, Exception? halted)
    {
        var stores = report.Stores;
        var removed = stores.Count(store => store.Failure is null);
        var notAttempted = report.NotAttempted > 0
            ? $", {report.NotAttempted} claim(s) not attempted"
            : string.Empty;
        AnsiConsole.MarkupLine(halted is null
            ? $"Dropped {(report.IsLinked ? "worktree" : "main worktree")} store{(stores.Count > 1 ? "s" : "")} " +
              $"for {report.RootPath.EscapeMarkup()}"
            : $"Dropped {removed} of {stores.Count} store(s) for {report.RootPath.EscapeMarkup()}" +
              notAttempted + ", then stopped");
        AnsiConsole.MarkupLine(
            $"  branch: {(report.Branch ?? (report.IsLinked ? "detached or unknown" : "not a lane")).EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  parent: {(report.ParentRootPath ?? "unknown").EscapeMarkup()}");
        if (stores.Count > 1)
        {
            AnsiConsole.MarkupLine(
                $"  folded: {stores.Count} registry rows of one tree, {removed} store(s) cleared — a spelling " +
                "difference is not a second workspace");
        }

        foreach (var store in stores)
        {
            var named = $"{store.StoreKey.EscapeMarkup()} ({store.Name.EscapeMarkup()}) registered at " +
                        $"{store.RegisteredRootPath.EscapeMarkup()}";
            if (store.Failure is { } failure)
            {
                AnsiConsole.MarkupLine($"  [red]x[/] not removed: {named} — {failure.EscapeMarkup()}");
                continue;
            }

            AnsiConsole.MarkupLine($"  store [bold]{named}[/]");
            AnsiConsole.MarkupLine($"    collection: {CollectionText(store)}");
            foreach (var path in store.RemovedPaths)
            {
                AnsiConsole.MarkupLine($"    removed: {path.EscapeMarkup()}");
            }

            if (store.RemovedPaths.Count == 0)
            {
                AnsiConsole.MarkupLine("    removed: registry row only, no store dir was there");
            }
        }

        if (report.DependentLanes > 0)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]{report.DependentLanes} lane row(s) still name this root as their parent[/] — their " +
                $"stores are untouched; {RemedyText.WorkspaceRepairCommand.EscapeMarkup()} reaps them once their " +
                "trees are gone");
        }
    }

    private static string CollectionText(WorktreeDroppedStore store) =>
        store.CollectionState switch
        {
            WorkspaceStoreDropper.CollectionRemoved => $"{store.Collection!.EscapeMarkup()} — deleted",
            WorkspaceStoreDropper.CollectionUnavailable =>
                $"{store.Collection!.EscapeMarkup()} — left in place, Qdrant did not answer",
            WorkspaceStoreDropper.CollectionFailed =>
                $"{store.Collection!.EscapeMarkup()} — delete refused by Qdrant",
            _ => "no Qdrant config at this root, nothing was asked of any collection",
        };
}
