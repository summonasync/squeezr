using Squeezr.Cli.Mcp;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli;

// The store axis of a query. `--workspace <storeKey|path>` and its `--at` sugar choose WHICH store answers;
// `--repo` chooses which repo inside that store is read. The two never trade places, and that ordering is the
// whole safety of pointing a query at another tree: the coordinates come from the selected store's index, and
// the repo filter is applied inside it — so naming a repo the selected store does not own is a refusal, never a
// silent fall back to the checkout the caller happens to stand in.
//
// Nothing here writes. A path spelling goes through `WorktreeResolve`, the read-only door T9 shipped, and its
// Gone / Unregistered / NotAWorktree sentences come back as they were: a targeting verb that minted a store to
// answer with would create exactly the litter the feature's death path exists to reap.
public static class WorkspaceTargetSelector
{
    public static WorkspaceTarget Require(string? workspace, string? at)
    {
        if (workspace is not null && at is not null)
        {
            throw new WorkspaceException(
                $"--workspace '{workspace}' and --at '{at}' both name a store, and one query answers from one. "
                + "Pass the store key or the path, or --at parent to point at this checkout's parent.",
                WorkspaceException.InvalidQueryCode);
        }

        if (workspace is null && at is null)
        {
            return Name(WorkspaceRootResolver.RequireWorkspaceRoot());
        }

        var root = at is not null
            ? string.Equals(at.Trim(), "parent", StringComparison.OrdinalIgnoreCase)
                ? ParentStoreOf(WorkspaceRootResolver.LaunchDirectory())
                : StoreAtResolvedPath(at)
            : StoreByKeyOrPath(workspace!);

        return Name(root);
    }

    // Every answer carries the same naming, targeted or not: a lane whose own store answers must still say which
    // branch it is describing, because the rows it returns do not tell the reader that by themselves.
    private static WorkspaceTarget Name(string root) =>
        new(root, AnswerSubject.Describe(root, WorkspaceRootResolver.LaunchDirectory()));

    // A key is tried first because it is the exact spelling the registry owns; a directory is tried next because
    // a lane is most often named by its checkout. A value that is neither is the refusal `--workspace` owes, and
    // its remedy names the two doors that could have produced the store: `workspace list` shows what exists,
    // `worktree init` makes a missing one.
    private static string StoreByKeyOrPath(string spelling)
    {
        var byKey = WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.FindByStoreKey(spelling));
        if (byKey is not null)
        {
            return byKey.RootPath;
        }

        var directory = ResolveAgainstLaunch(spelling);
        if (Directory.Exists(directory))
        {
            return StoreAtResolvedPath(directory);
        }

        throw new WorkspaceException(
            $"'{spelling}' is no registered store key and no directory '{SessionLocation.Display(directory)}'. "
            + $"Run '{RemedyText.WorkspaceListCommand}' for the keys, or {RemedyText.WorktreeInitCommand} in a "
            + "checkout that has no store yet.",
            WorkspaceException.WorktreeNotRegisteredCode);
    }

    private static string StoreAtResolvedPath(string spelling)
    {
        var outcome = WorkspaceRootResolver.GuardRegistryIo(
            () => WorktreeResolve.Resolve(ResolveAgainstLaunch(spelling)));

        if (outcome.State != WorktreeResolveState.Resolved)
        {
            throw new WorkspaceException(
                outcome.Reason ?? $"'{spelling}' resolves to no store.",
                outcome.State == WorktreeResolveState.Unregistered
                    ? WorkspaceException.WorktreeNotRegisteredCode
                    : WorkspaceException.NotAWorktreeCode);
        }

        // One checkout can carry rows under several spellings. The directory is the identity, so the first row's
        // root answers and every spelling of it reads one store — the same fold the read verb reports.
        return outcome.Rows.Count > 0
            ? outcome.Rows[0].RootPath
            : outcome.RootPath ?? WorkspaceCanonicalPath.Canonicalize(ResolveAgainstLaunch(spelling));
    }

    // The parent of the caller's own checkout, then that parent's store — the two steps the sugar promises. A
    // main worktree has no parent, and a parent nobody indexed is answered with the read door's sentence,
    // because the missing half there is a registration, not a mistyping.
    private static string ParentStoreOf(string launchDirectory)
    {
        var own = WorkspaceRootResolver.GuardRegistryIo(() => WorktreeResolve.Resolve(launchDirectory));

        if (own.ParentRootPath is not { Length: > 0 } parent)
        {
            throw new WorkspaceException(
                $"'{SessionLocation.Display(own.PhysicalPath)}' is not a linked checkout, so it has no parent "
                + "store to point a query at. Name the store with --workspace <storeKey> instead.",
                WorkspaceException.NotAWorktreeCode);
        }

        if (!own.ParentRegistered)
        {
            throw new WorkspaceException(
                $"the parent checkout '{SessionLocation.Display(parent)}' owns no store, so --at parent has "
                + $"nothing to answer from. Run {RemedyText.InitCommand} there first"
                + (own.Rows.Count > 0
                    ? " — this checkout already has its own store to answer from."
                    : $", then {RemedyText.WorktreeInitCommand} here to fill a lane store from it."),
                WorkspaceException.WorktreeNotRegisteredCode);
        }

        return StoreAtResolvedPath(parent);
    }

    private static string ResolveAgainstLaunch(string spelling) =>
        Path.IsPathRooted(spelling)
            ? spelling
            : Path.GetFullPath(Path.Combine(WorkspaceRootResolver.LaunchDirectory(), spelling));
}

public sealed record WorkspaceTarget(string WorkspaceRoot, AnswerSubject? Subject)
{
    // The naming rides the warning channel in both modes, the way `worktree resolve' sends its pending notices:
    // a reader that parses stdout keeps the shape it already knows, and a reader that only reads stderr still
    // learns which tree the coordinates came from. A single-root answer from the caller's own checkout prints
    // nothing at all — there is one tree in play, and the caller is standing in it.
    public void Report() => AnswerSubjectNotice.Report(Subject);
}
