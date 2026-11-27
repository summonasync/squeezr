using Squeezr.Lite.Mcp;
using Squeezr.Lite.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Commands;

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

    private static string ValueAdvice(string option) => option == AtOption
        ? "Give it a path inside the checkout to read, or 'parent' for this checkout's parent store."
        : "Give it a registered store key, or a path inside the checkout that owns the store.";

    private static WorkspaceTarget Name(string root) =>
        new(root, AnswerSubject.Describe(root, WorkspaceRootResolver.LaunchDirectory()));

    private static string StoreByKeyOrPath(string spelling)
    {
        var byKey = WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.FindByStoreKey(spelling));
        if (byKey is not null)
        {
            return StoreAtResolvedPath(byKey.RootPath);
        }

        var directory = ResolveAgainstLaunch(spelling);
        if (Directory.Exists(directory) || RegistryClaimsThePath(directory))
        {
            return StoreAtResolvedPath(directory);
        }

        throw new WorkspaceException(
            $"'{spelling}' is no registered store key and no directory '{SessionLocation.Display(directory)}'. "
            + "Run 'squeezr workspace list' for the keys, or 'squeezr worktree init' in a "
            + "checkout that has no store yet.",
            WorkspaceException.WorktreeNotRegisteredCode);
    }

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

        return outcome.Rows.Count > 0
            ? outcome.Rows[0].RootPath
            : outcome.RootPath ?? WorkspaceCanonicalPath.Canonicalize(ResolveAgainstLaunch(spelling));
    }

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
                + $"nothing to answer from. Run {LiteRemedy.InitCommand} there first"
                + (own.Rows.Count > 0
                    ? " — this checkout already has its own store to answer from."
                    : ", then 'squeezr worktree init' here to fill a lane store from it."),
                WorkspaceException.WorktreeNotRegisteredCode);
        }

        return StoreAtResolvedPath(parent);
    }

    private static string ResolveAgainstLaunch(string spelling) =>
        Path.IsPathRooted(spelling)
            ? spelling
            : Path.GetFullPath(Path.Combine(WorkspaceRootResolver.LaunchDirectory(), spelling));
}
