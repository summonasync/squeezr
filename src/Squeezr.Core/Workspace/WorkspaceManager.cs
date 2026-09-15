using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public sealed class WorkspaceManager
{
    private readonly string _root;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public WorkspaceManager(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new WorkspaceException($"Directory '{root}' does not exist.");
        }
        _root = Path.GetFullPath(root);
    }

    public InitResult Init(string? name)
    {
        if (WorkspaceManifestFile.Exists(_root))
        {
            throw new WorkspaceException($"Workspace is already initialized at '{_root}'.");
        }

        var workspaceName = string.IsNullOrWhiteSpace(name)
            ? new DirectoryInfo(_root).Name
            : name.Trim();
        ValidateName(workspaceName, "Workspace");

        Directory.CreateDirectory(SqueezrPaths.StoreDir(_root));
        ConfigFile.Save(_root, SqueezrConfig.CreateDefault());
        StateFile.Save(_root, new IndexState());
        using (new MetaStore(SqueezrPaths.MetaDbPath(_root), createIfMissing: true))
        {
        }

        WorkspaceManifestFile.Save(_root, new WorkspaceManifest { Name = workspaceName });
        DockerComposeFile.Write(_root);

        return new InitResult(workspaceName, _root);
    }

    public RepoEntry AddRepo(string path, string? name, Action<string>? onStaleTakeover = null)
    {
        var manifest = LoadManifest();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkspaceException("Repository path is required.");
        }

        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        if (!Directory.Exists(fullPath))
        {
            throw new WorkspaceException($"Repository path '{fullPath}' does not exist.");
        }

        var repoName = string.IsNullOrWhiteSpace(name)
            ? new DirectoryInfo(fullPath).Name
            : name.Trim();
        ValidateName(repoName, "Repository");

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(_root), onStaleTakeover))
        {
            if (manifest.Repos.Any(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkspaceException($"Repository '{repoName}' already exists in workspace '{manifest.Name}'.");
            }

            if (manifest.Repos.Any(r => string.Equals(ToFullPath(r.Path), fullPath, PathComparison)))
            {
                throw new WorkspaceException($"Path '{fullPath}' is already added to the workspace.");
            }

            var entry = new RepoEntry(repoName, ToStoredPath(fullPath));
            WorkspaceManifestFile.Save(_root, manifest with { Repos = [..manifest.Repos, entry] });

            using var store = new MetaStore(SqueezrPaths.MetaDbPath(_root));
            store.AddRepo(new RepoDocument { Id = store.GetNextRepoId(), Name = entry.Name, Path = entry.Path });
            return entry;
        }
    }

    public WorkspaceStatus GetStatus()
    {
        var manifest = LoadManifest();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_root));
        var repos = manifest.Repos
            .Select(r =>
            {
                var state = store.GetIndexState(r.Name);
                return new RepoStatus(
                    r.Name,
                    r.Path,
                    state?.Stats.Files ?? 0,
                    state?.Stats.Symbols ?? 0,
                    state?.Stats.Chunks ?? 0,
                    state?.LastFullIndexAt ?? store.FindRepo(r.Name)?.LastIndexedAt);
            })
            .ToList();

        return new WorkspaceStatus(manifest.Name, repos);
    }

    public IReadOnlyList<RepoEntry> ListRepos()
    {
        var manifest = LoadManifest();
        return manifest.Repos;
    }

    public DropResult Drop(Action<string>? onStaleTakeover = null)
    {
        var storeDir = SqueezrPaths.StoreDir(_root);
        if (!Directory.Exists(storeDir))
        {
            throw new WorkspaceException("Nothing to drop: no '.squeezr' store in this directory.");
        }

        var removed = new List<string>();

        // Windows refuses to delete a directory that holds an open file,
        // so the lock is released (which removes index.lock) before the delete.
        using (IndexLock.Acquire(storeDir, onStaleTakeover))
        {
        }

        Directory.Delete(storeDir, recursive: true);
        removed.Add(SqueezrPaths.StoreDirName);

        if (File.Exists(SqueezrPaths.ManifestPath(_root)))
        {
            File.Delete(SqueezrPaths.ManifestPath(_root));
            removed.Add(SqueezrPaths.ManifestFileName);
        }

        if (File.Exists(SqueezrPaths.ComposePath(_root)))
        {
            File.Delete(SqueezrPaths.ComposePath(_root));
            removed.Add(SqueezrPaths.ComposeFileName);
        }

        return new DropResult(removed.ToArray(), "docker compose down -v");
    }

    private WorkspaceManifest LoadManifest() =>
        WorkspaceManifestFile.Exists(_root)
            ? WorkspaceManifestFile.Load(_root)
            : throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr init' first.",
                WorkspaceException.NotInitializedCode);

    private string ToStoredPath(string fullPath)
    {
        if (string.Equals(fullPath, _root, PathComparison))
        {
            return ".";
        }

        var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, PathComparison)
            ? Path.GetRelativePath(_root, fullPath).Replace('\\', '/')
            : fullPath.Replace('\\', '/');
    }

    private string ToFullPath(string storedPath) =>
        Path.GetFullPath(Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(_root, storedPath));

    private static void ValidateName(string name, string subject)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new WorkspaceException($"{subject} name '{name}' is not valid.");
        }
    }
}
