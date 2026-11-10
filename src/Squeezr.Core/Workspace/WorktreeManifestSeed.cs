using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// A copied database is only queryable while the child manifest names the rows it holds: every read leg
// joins a meta.db repo row to a manifest entry BY NAME and composes the file path out of that entry
// (ExactSearchPlan, IdentifierTokenLeg, SymbolLookup, NavigationContext), and the indexer reaps a file
// row whose name the manifest does not carry as RepositoryGone. Filling bytes without seeding this file
// installs a store nothing can read, and the obvious repair — 'squeezr add .' in the lane — mints a
// second row for the same tree, which is what drops the copied files on the next pass.
//
// Identity here is the name, never a new one: the repo names in the copied rows are the keys the trigram
// (repoId, fileId) pairs and the reap path already speak, so a seeded entry is the main worktree's row
// with its path re-based under the lane root. A row whose path cannot be re-based inside the lane is
// refused and said out loud instead of written: an absolute main-worktree path in a lane manifest is the
// cross-tree answer this feature exists to kill. Entries the lane already owns win, so a pass that died
// between the swap and this write can re-run without duplicating a name — and a duplicated name is the
// state every read leg's name-keyed dictionary refuses to load.
public static class WorktreeManifestSeed
{
    public sealed record Result(IReadOnlyList<string> Owned, IReadOnlyList<string> Unowned);

    public static Result OwnCopiedRows(
        string targetManifestPath,
        string targetRoot,
        IReadOnlyList<RepoEntry> sourceRepos,
        IReadOnlyList<string> installedRepoNames)
    {
        var owned = new List<string>();
        var unowned = new List<string>();
        if (installedRepoNames.Count == 0)
        {
            return new Result(owned, unowned);
        }

        var manifest = File.Exists(targetManifestPath)
            ? WorkspaceManifestFile.LoadAt(targetManifestPath)
            : new WorkspaceManifest
            {
                Name = new DirectoryInfo(targetRoot).Name,
                RootPath = WorkspaceCanonicalPath.Canonicalize(targetRoot),
            };

        var sourceByName = sourceRepos.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var entries = manifest.Repos.ToList();
        var laneRoot = WorkspaceCanonicalPath.ResolveThroughLinks(targetRoot);

        foreach (var name in installedRepoNames)
        {
            if (entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!sourceByName.TryGetValue(name, out var source))
            {
                unowned.Add($"'{name}' has rows in the copied database but no entry in the main worktree's " +
                    "manifest, so this worktree cannot say where its files are; run 'squeezr workspace repair'.");
                continue;
            }

            if (!TryLaneRelative(source.Path, laneRoot, name, out var relativePath, out var reason))
            {
                unowned.Add(reason);
                continue;
            }

            entries.Add(new RepoEntry(source.Name, relativePath));
            owned.Add(source.Name);
        }

        if (owned.Count > 0)
        {
            WorkspaceManifestFile.SaveAt(targetManifestPath, manifest with { Repos = entries });
        }

        return new Result(owned, unowned);
    }

    private static bool TryLaneRelative(
        string storedPath,
        string laneRoot,
        string repoName,
        out string relativePath,
        out string reason)
    {
        var absolute = Path.IsPathRooted(storedPath)
            ? Path.GetFullPath(storedPath)
            : Path.GetFullPath(Path.Combine(laneRoot, storedPath));

        if (!WorkspaceCanonicalPath.IsInsidePhysicalDirectory(laneRoot, absolute))
        {
            reason = Path.IsPathRooted(storedPath)
                ? $"'{repoName}' is stored as the absolute path '{storedPath}', which is outside this " +
                  $"worktree at '{laneRoot}'. Its rows were copied but this worktree does not own them: a " +
                  "main-worktree path in a lane manifest makes the lane answer with files it does not " +
                  $"belong to. Add the directory here if the lane is meant to index it: {RemedyText.AddCommand} <path> --name {repoName}."
                : $"'{repoName}' is stored as '{storedPath}', which resolves outside this worktree at " +
                  $"'{laneRoot}'. Its rows were copied but this worktree does not own them; re-add the " +
                  $"directory inside the tree: {RemedyText.AddCommand} <path> --name {repoName}.";
            relativePath = string.Empty;
            return false;
        }

        if (!Directory.Exists(absolute))
        {
            reason = $"'{repoName}' maps to '{absolute}', which this worktree's checkout does not hold. " +
                $"Its rows were copied but nothing here answers for them; index the lane's own tree with " +
                $"{RemedyText.AddCommand} <path> --name {repoName} once the directory exists.";
            relativePath = string.Empty;
            return false;
        }

        relativePath = string.Equals(absolute, laneRoot, PathComparison)
            ? "."
            : Path.GetRelativePath(laneRoot, absolute).Replace('\\', '/');
        reason = string.Empty;
        return true;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
