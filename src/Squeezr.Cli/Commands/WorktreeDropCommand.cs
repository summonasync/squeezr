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
            var stores = new List<WorktreeDroppedStore>();
            Exception? halted = null;
            foreach (var claim in plan.Claims)
            {
                // The handle is opened before the row goes, the collection is deleted after: a store whose
                // config cannot be read is dropped anyway and says so, because the vectors are the smaller
                // loss to leave behind than a registry row that names a directory nothing can open.
                //
                // A fold can stop halfway: one store of one tree is deletable, the other is not. The rows
                // already gone are gone from the registry, so they are reported as facts of this run instead
                // of being lost with the exception — the shape the repair sweep already uses.
                // One door for both verbs that end a store: the claim is resolved by path, the removal runs by
                // key through the same DropByStoreKey the old verb uses, so a worktree drop and
                // `drop --store-key` cannot drift into two different kinds of death.
                try
                {
                    var result = await WorkspaceStoreDropper.DropByStoreKeyAsync(claim.StoreKey, cancellationToken);
                    stores.Add(new WorktreeDroppedStore(
                        claim.StoreKey,
                        claim.Name,
                        claim.RegisteredRootPath,
                        result.RemovedPaths,
                        result.Collection,
                        result.CollectionState));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stores.Add(FailedClaim(claim, ex));
                    halted = ex;
                    break;
                }
            }

            var report = new WorktreeDropReport(
                plan.RootPath, plan.Branch, plan.ParentRootPath, plan.IsLinked, plan.DependentLanes, stores);

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
        AnsiConsole.MarkupLine(halted is null
            ? $"Dropped {(report.IsLinked ? "worktree" : "main worktree")} store{(stores.Count > 1 ? "s" : "")} " +
              $"for {report.RootPath.EscapeMarkup()}"
            : $"Dropped {removed} of {stores.Count} store(s) for {report.RootPath.EscapeMarkup()}, then stopped");
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

    // The removal primitive tags both phases, and the tag is what the reader needs: a store-side tag means
    // nothing disappeared, an unverified tag means a store dir did disappear while its row stayed. Raw
    // exception text carries paths this pass did not remove, so the wording stays with the tag.
    private static WorktreeDroppedStore FailedClaim(WorktreeDropClaim claim, Exception ex)
    {
        IReadOnlyList<string> gone = ex is WorkspaceStoreRemovalUnverifiedException unverified
            ? unverified.RemovedStoreDirs
            : [];
        var reason = ex switch
        {
            WorkspaceStoreRemovalUnverifiedException => "the store dir was removed, the registry row could not be pruned",
            WorkspaceStoreRemovalException => "a store dir could not be removed",
            UnauthorizedAccessException => "permission denied on the store path",
            _ => $"{ex.GetType().Name}: the store could not be removed",
        };
        return new WorktreeDroppedStore(
            claim.StoreKey,
            claim.Name,
            claim.RegisteredRootPath,
            gone,
            null,
            WorkspaceStoreDropper.CollectionNotConfigured,
            reason);
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
