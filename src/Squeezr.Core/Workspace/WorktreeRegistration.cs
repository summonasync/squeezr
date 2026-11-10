using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// A git worktree is a checkout with its own toplevel and a shared git dir, so it wants its own store
// and still belongs to a main worktree that is already indexed. This is the deliberate registration
// of that child: it reads git's facts, refuses to invent a parent, and never touches a store it did
// not create. Identity is the canonical root — never the name — because one tree can be spelled two
// ways at once: the folder it sits in and the branch it holds, and a linked ancestor keeps a third.
public static class WorktreeRegistration
{
    public static WorktreeRegistrationOutcome Register(string path, string? requestedName)
    {
        var name = string.IsNullOrWhiteSpace(requestedName) ? null : requestedName.Trim();
        var facts = WorktreeFacts.Resolve(path)
            ?? throw new WorkspaceException(
                $"'{path}' is not a git worktree: no readable .git marker up the tree. Register it as a " +
                $"plain workspace with '{RemedyText.InitCommand}'.",
                WorkspaceException.NotAWorktreeCode);

        // The row holds the PHYSICAL root: a worktree registered under a link-shaped path would otherwise
        // be unreachable by cwd-based discovery (getcwd is physical on Linux), which hands an agent the
        // parent's coordinates for a tree that has its own store — the defect this feature exists to kill.
        var childRoot = WorkspaceCanonicalPath.ResolveThroughLinks(facts.Toplevel);
        var parentRoot = facts.MainWorktreeRoot is null
            ? null
            : WorkspaceCanonicalPath.Canonicalize(facts.MainWorktreeRoot);
        var existing = WorkspaceRegistry.FindByRootPath(childRoot)
            ?? WorkspaceRegistry.FindByAnyRootSpelling(childRoot);

        return existing is null
            ? Mint(facts, childRoot, parentRoot, name)
            : Adopt(facts, existing, name);
    }

    private static WorktreeRegistrationOutcome Mint(
        WorktreeFacts facts,
        string childRoot,
        string? parentRoot,
        string? name)
    {
        if (parentRoot is null)
        {
            return WorktreeRegistrationOutcome.NothingCreated(
                WorktreeRegistrationState.NoParentRootToName, childRoot, facts, NoParentRootToName(facts));
        }

        if (WorkspaceRegistry.FindByRootPath(parentRoot) is null)
        {
            return WorktreeRegistrationOutcome.NothingCreated(
                WorktreeRegistrationState.ParentNotIndexed,
                childRoot,
                facts,
                ParentNotIndexed(parentRoot, childRoot, facts.IsLinked));
        }

        // Init's own adoption pass already classifies an orphan store pointing at the same root; the
        // worktree verb has no business swallowing that warning.
        var init = new WorkspaceManager(childRoot).Init(name ?? facts.StoreName);

        var created = WorkspaceRegistry.FindByRootPath(childRoot)
            ?? throw new WorkspaceException(
                $"'{childRoot}' was registered and then disappeared from the registry in the same pass, so it " +
                $"is not linked to '{parentRoot}' yet. Re-run the command; if the row keeps disappearing run " +
                "'squeezr workspace repair'.");

        var linked = created with
        {
            ParentRootPath = parentRoot,
            Kind = WorkspaceKind.Worktree,
            Branch = facts.Branch,
        };
        WorkspaceRegistry.Add(linked);

        return WorktreeRegistrationOutcome.FromRow(
            WorktreeRegistrationState.Registered, linked, facts, repairHint: init.RepairHint);
    }

    private static WorktreeRegistrationOutcome Adopt(
        WorktreeFacts facts,
        WorkspaceRegistryEntry existing,
        string? name)
    {
        if (name is not null)
        {
            var intended = WorkspaceStoreKeys.Create(name, existing.RootPath);
            if (!string.Equals(existing.StoreKey, intended, StringComparison.Ordinal))
            {
                throw new WorkspaceException(NameClash(existing, name, intended), WorkspaceException.WorktreeNameClashCode);
            }
        }

        // A primary checkout has no git parent to record and its row is already what plain init wrote,
        // so the idempotent answer reports the existing row and rewrites nothing.
        if (!facts.IsLinked)
        {
            return WorktreeRegistrationOutcome.FromRow(
                WorktreeRegistrationState.AlreadyRegistered, existing, facts, MainWorktree(existing));
        }

        // Kind and branch come off git verbatim — a stale branch claim is a lie. The parent link is the
        // exception: files that cannot name a main root now do not prove the pass that measured one was
        // wrong, and this column is what drop, repair and resolve hang a worktree's death on.
        var stamped = existing with
        {
            ParentRootPath = facts.MainWorktreeRoot is null
                ? existing.ParentRootPath
                : WorkspaceCanonicalPath.Canonicalize(facts.MainWorktreeRoot),
            Kind = WorkspaceKind.Worktree,
            Branch = facts.Branch,
        };
        // This verb runs at every session start, so a pass that changes nothing must not rewrite the
        // whole registry, reorder the row or rotate the backup file.
        if (stamped == existing)
        {
            return WorktreeRegistrationOutcome.FromRow(
                WorktreeRegistrationState.AlreadyRegistered, existing, facts);
        }

        WorkspaceRegistry.Add(stamped);

        return WorktreeRegistrationOutcome.FromRow(WorktreeRegistrationState.AlreadyRegistered, stamped, facts);
    }

    private static string NoParentRootToName(WorktreeFacts facts) =>
        $"Cannot find the main worktree root of '{facts.Toplevel}': its common git dir '{facts.CommonDir}' is " +
        "not spelled '<main root>/.git', so git records no parent to link to. Nothing was created.";

    private static string ParentNotIndexed(string parentRoot, string childRoot, bool isLinked) =>
        isLinked
            ? $"Main worktree root '{parentRoot}' is not an indexed workspace, so there is nothing for the " +
              $"worktree at '{childRoot}' to be filled from. Run '{RemedyText.InitCommand}' in '{parentRoot}' " +
              "first, then re-run this command. Nothing was created."
            : $"'{parentRoot}' is the main worktree and it is not an indexed workspace. Register it with " +
              $"'{RemedyText.InitCommand}' — that is the verb for a main worktree, this one only gives a " +
              "linked checkout its own store. Nothing was created.";

    private static string MainWorktree(WorkspaceRegistryEntry existing) =>
        $"'{existing.RootPath}' is the main worktree, already registered as '{existing.StoreKey}'. " +
        "A linked checkout gets its own store; this one keeps the store it has.";

    private static string NameClash(WorkspaceRegistryEntry existing, string name, string intended) =>
        $"'{existing.RootPath}' is already registered as store '{existing.StoreKey}', but --name '{name}' asks " +
        $"for '{intended}'. One worktree gets one store — two keys for one root is the identity fork this verb " +
        $"exists to close. Drop the store and register again: {RemedyText.DropStore(existing.StoreKey)}. Or " +
        $"re-run with --name '{existing.Name}' to keep the store this root already has.";
}
