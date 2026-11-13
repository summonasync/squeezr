using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The second tree in one lane store, which is the shape spec 10 chose over a negation flag: the lane's own
// checkout stays the default subject of every answer, and the main worktree becomes reachable through the selector
// the product already has (`--repo main`), because a query's subject is a fact the envelope carries rather than a
// memory the agent holds. The name is always `main` — one lane, one parent, no name to guess.
//
// WorktreeManifestSeed refuses an absolute main-worktree path in a lane manifest, and it is right to: a *copied*
// row that reads outside the lane makes the lane answer with files it does not own. This door writes one on
// purpose — behind an explicit flag, with the price named before it is paid, with every row it produces marked
// with the repo it came from. The difference is not the path shape, it is that nobody asked for the seed's version.
//
// Registration goes through WorkspaceManager.AddRepo, the rooted-path door `squeezr add` already uses, so this
// verb owns no second way to put a repo in a store: the manifest entry and the meta.db repo row land together,
// under the store's own index lock, exactly as they would for a human typing the command.
public static class WorktreeBaseline
{
    public const string RepoName = "main";

    public static WorktreeBaselineOutcome Register(
        string laneRoot,
        string requested,
        WorktreeFacts? facts,
        Action<string>? onStaleTakeover = null)
    {
        var name = requested.Trim();
        if (name.Length == 0 || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new WorkspaceException(
                $"'{requested}' names no baseline to register. Pass the main worktree's root or its workspace " +
                $"name{GitParentHint(facts)}.",
                WorkspaceException.InvalidQueryCode);
        }

        var baselineRoot = Resolve(laneRoot, name, facts);
        if (WorkspaceCanonicalPath.SamePhysicalPath(baselineRoot, laneRoot))
        {
            throw new WorkspaceException(
                $"--baseline '{requested}' resolves to '{baselineRoot}', which is the checkout this store already " +
                "answers for. One store owning the same tree under two repo names is the identity fork this " +
                "feature exists to close — index the lane alone by leaving --baseline out.",
                WorkspaceException.WorktreeSameStoreCode);
        }

        var costNotice = BuildCostNotice(baselineRoot, laneRoot);

        var manifest = WorkspaceManifestFile.Exists(laneRoot)
            ? WorkspaceManifestFile.Load(laneRoot)
            : throw new WorkspaceException(
                $"--baseline needs a manifest at '{laneRoot}' to add repo '{RepoName}' to, and no store was " +
                $"created here. Register this checkout first with {RemedyText.WorktreeInitCommand}.",
                WorkspaceException.NotInitializedCode);
        var existing = manifest.Repos
            .FirstOrDefault(repo => string.Equals(repo.Name, RepoName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var existingRoot = WorkspaceCanonicalPath.ResolveThroughLinks(
                Path.IsPathRooted(existing.Path)
                    ? existing.Path
                    : Path.Combine(laneRoot, existing.Path));
            if (WorkspaceCanonicalPath.SamePhysicalPath(existingRoot, baselineRoot))
            {
                return WorktreeBaselineOutcome.AlreadyThere(laneRoot, baselineRoot,
                    BuildStandingCostNotice(baselineRoot, laneRoot));
            }

            throw new WorkspaceException(
                $"repo '{RepoName}' in '{laneRoot}' already names '{existingRoot}', so --baseline cannot take that " +
                $"name for '{baselineRoot}'. One repo name pointing at two trees is what makes an answer lie about " +
                $"which one it read: drop this store with {RemedyText.DropCommand} and re-run this verb, or keep " +
                "the parent out of the lane's index by leaving --baseline out.",
                WorkspaceException.WorktreeNameClashCode);
        }

        new WorkspaceManager(laneRoot).AddRepo(baselineRoot, RepoName, onStaleTakeover);

        // The row changes what the store owns, so it changes what its proof may claim. A record closed by a pass
        // that ran over one manifest does not cover a second tree that pass never read, and the reconcile pass
        // decides whether it owes a run by consulting that record — without this write the baseline repo would be
        // registered and never indexed, while the lane went on answering 'reconciled' over half its own manifest.
        WorkspaceManifestFile.WriteReconcileAt(
            SqueezrPaths.ManifestPath(laneRoot), laneRoot,
            (WorkspaceManifestFile.TryRead(laneRoot) ?? ReconcileMarker.Opened()).WidenedToBaseline());
        return WorktreeBaselineOutcome.Registered(laneRoot, baselineRoot, costNotice);
    }

    // The price, in the door's own words, so the sentence that goes out before the pass and the one that rides
    // the report cannot drift apart into two claims about one cost. Both trees are named because the promise is
    // about both of them: a notice that only says 'this lane' leaves a reader unable to tell WHICH checkout is
    // about to be indexed a second time, in the store they are standing in.
    public static string BuildCostNotice(string baselineRoot, string laneRoot) =>
        $"'{baselineRoot}' joins '{laneRoot}' as repo '{RepoName}': the reconcile pass this verb runs now indexes " +
        "BOTH trees, so the lane's index roughly doubles — the main worktree's files on top of this branch's. " +
        "Ask for it only when a query needs the parent's coordinates.";

    // The same price told in the present tense, because nothing here is about to be paid that was not paid
    // already: the row stands, the store answers for two trees, and a pass that ran before the row existed has
    // nothing left to tell this lane about the second one. Saying 'this pass doubles your index' over a store
    // that doubled it in an earlier session would be a warning about work nobody is doing.
    public static string BuildStandingCostNotice(string baselineRoot, string laneRoot) =>
        $"'{laneRoot}' already owns repo '{RepoName}' pointing at '{baselineRoot}': this store answers for TWO " +
        "trees and its index is roughly double what one branch costs. No row is registered again and no pass is " +
        "owed by this call — the proof on the store says what it measured.";

    // A name is tried against the registry first, because the flag's grammar says `<name|path>` and a workspace
    // name is what `workspace list` prints; anything that is not one is tried as a directory, resolved against the
    // lane root the way `squeezr add` resolves it. A value that is neither is a refusal, never a row: an absolute
    // path written into a manifest for a directory that does not exist is a repo nothing can answer for.
    private static string Resolve(string laneRoot, string requested, WorktreeFacts? facts)
    {
        var named = WorkspaceRegistry.Load()
            .FirstOrDefault(row => string.Equals(row.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return WorkspaceCanonicalPath.ResolveThroughLinks(named.RootPath);
        }

        var candidate = Path.GetFullPath(
            Path.IsPathRooted(requested) ? requested : Path.Combine(laneRoot, requested));
        if (Directory.Exists(candidate))
        {
            return WorkspaceCanonicalPath.ResolveThroughLinks(candidate);
        }

        throw new WorkspaceException(
            $"--baseline '{requested}' is no registered workspace and no directory{GitParentHint(facts)}. Pass the " +
            "main worktree's root, or leave --baseline out to index only this checkout.",
            WorkspaceException.InvalidQueryCode);
    }

    private static string GitParentHint(WorktreeFacts? facts) =>
        facts?.MainWorktreeRoot is { } mainRoot
            ? $" (git says this checkout hangs under '{mainRoot}')"
            : string.Empty;
}
