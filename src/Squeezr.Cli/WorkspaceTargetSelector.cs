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
    private const string WorkspaceOption = "--workspace";
    private const string AtOption = "--at";

    public static WorkspaceTarget Require(string? workspace, string? at)
    {
        RequireValue(workspace, WorkspaceOption);
        RequireValue(at, AtOption);

        if (workspace is not null && at is not null)
        {
            throw new WorkspaceException(
                $"{WorkspaceOption} '{workspace}' and {AtOption} '{at}' both name a store, and one query answers from one. "
                + $"Pass the store key or the path, or {AtOption} parent to point at this checkout's parent.",
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

    // An option passed with nothing in it names no store, and the honest answer is to say so: falling back on the
    // nearest store would make an unfilled flag mean the opposite of a missing one, and the caller would read
    // rows out of the tree it was trying to leave behind.
    private static void RequireValue(string? spelling, string option)
    {
        if (spelling is not null && spelling.Trim().Length == 0)
        {
            throw new WorkspaceException(
                $"'{option}' was passed with no value, so it names no store. {ValueAdvice(option)} "
                + $"To answer from the nearest store, drop {option} instead of emptying it.",
                WorkspaceException.InvalidQueryCode);
        }
    }

    // The advice has to name a spelling the option actually takes: `--at' reads a checkout or the sugar, never a
    // store key, and a remedy pointing at a door that option does not have is the same defect this round closed
    // on the gone-tree side.
    private static string ValueAdvice(string option) => option == AtOption
        ? "Give it a path inside the checkout to read, or 'parent' for this checkout's parent store."
        : "Give it a registered store key, or a path inside the checkout that owns the store.";

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
            // A row is a claim about a tree, not the tree. A lane whose checkout was removed keeps its row until
            // `workspace repair' reaps it, and handing the dead root straight back sent the query to a deeper
            // engine that answered with an unremedied workspace_error. The read door owns the gone sentence —
            // the surviving keys, the reap remedy — so one dead tree is refused the same words from either side.
            return StoreAtResolvedPath(byKey.RootPath);
        }

        var directory = ResolveAgainstLaunch(spelling);
        if (Directory.Exists(directory) || RegistryClaimsThePath(directory))
        {
            return StoreAtResolvedPath(directory);
        }

        throw new WorkspaceException(
            $"'{spelling}' is no registered store key and no directory '{SessionLocation.Display(directory)}'. "
            + $"Run '{RemedyText.WorkspaceListCommand}' for the keys, or {RemedyText.WorktreeInitCommand} in a "
            + "checkout that has no store yet.",
            WorkspaceException.WorktreeNotRegisteredCode);
    }

    // A missing directory the registry still claims is the gone state, not a mistyping, and the two refusals
    // owe different remedies: one names the keys that survived and how to reap them, the other points at the
    // keys that exist. Without this check a deleted lane spelled as a path was told to run `worktree init' in
    // a checkout nothing can enter.
    private static bool RegistryClaimsThePath(string directory) =>
        WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.Load()
            .Any(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, directory)));

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
