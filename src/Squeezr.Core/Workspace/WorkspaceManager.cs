using System.Text.Json;
using Squeezr.Core.Configuration;
using Squeezr.Core.Infra;
using Squeezr.Core.Serialization;
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

        if (SqueezrHome.IsCentralStore(Path.Combine(_root, SqueezrPaths.StoreDirName)))
        {
            throw new WorkspaceException(
                $"Cannot initialize a workspace at '{_root}': the directory holds the central squeezr store ('{SqueezrHome.Resolve()}').");
        }

        var workspaceName = string.IsNullOrWhiteSpace(name)
            ? new DirectoryInfo(_root).Name
            : name.Trim();
        ValidateName(workspaceName, "Workspace");

        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(_root);
        var storeKey = WorkspaceStoreKeys.Create(workspaceName, canonicalRoot);
        var adopting = File.Exists(Path.Combine(SqueezrHome.WorkspacesDir, storeKey, SqueezrPaths.StoreManifestFileName));

        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            workspaceName,
            canonicalRoot,
            storeKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        // Steps below resolve paths through the registry, so a failure must roll
        // the registration back: an entry without a store would resolve to a missing dir.
        try
        {
            Directory.CreateDirectory(SqueezrPaths.StoreDir(_root));

            if (!adopting || !File.Exists(SqueezrPaths.ConfigPath(_root)))
            {
                ConfigFile.Save(_root, SqueezrConfig.CreateDefault());
            }

            using (new MetaStore(SqueezrPaths.MetaDbPath(_root), createIfMissing: true))
            {
            }

            if (adopting)
            {
                AdoptManifest(canonicalRoot);
            }
            else
            {
                WorkspaceManifestFile.Save(_root, new WorkspaceManifest { Name = workspaceName, RootPath = canonicalRoot });
            }
        }
        catch
        {
            WorkspaceRegistry.RemoveByRoot(_root);
            throw;
        }

        var repairHint = adopting ? FindNameMismatchHint(storeKey, canonicalRoot) : null;
        return new InitResult(workspaceName, _root, repairHint);
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
            WorkspaceManifestFile.Save(_root, manifest with { Repos = [.. manifest.Repos, entry] });

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
                    state?.LastFullIndexAt ?? store.FindRepo(r.Name)?.LastIndexedAt,
                    state?.Stats.IgnoredTotal ?? 0,
                    state?.Stats.IgnoredBySource);
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
        var wasRegistered = WorkspaceRegistry.TryGetStoreDir(_root, out _);
        var storeDir = SqueezrPaths.StoreDir(_root);
        if (!Directory.Exists(storeDir))
        {
            throw new WorkspaceException("Nothing to drop: no workspace store is registered for this directory.");
        }

        // Windows refuses to delete a directory that holds an open file,
        // so the lock is released (which removes index.lock) before the delete.
        using (IndexLock.Acquire(storeDir, onStaleTakeover))
        {
        }

        Directory.Delete(storeDir, recursive: true);
        var removed = new List<string> { storeDir };

        if (wasRegistered)
        {
            WorkspaceRegistry.RemoveByRoot(_root);
        }
        else
        {
            var legacyManifest = Path.Combine(_root, SqueezrPaths.ManifestFileName);
            if (File.Exists(legacyManifest))
            {
                File.Delete(legacyManifest);
                removed.Add(legacyManifest);
            }

            var composePath = SqueezrPaths.ComposePath(_root);
            if (ServicesComposeFile.IsSqueezrGenerated(composePath))
            {
                File.Delete(composePath);
                removed.Add(composePath);
            }
        }

        return new DropResult([.. removed], "squeezr services down");
    }

    private void AdoptManifest(string canonicalRoot)
    {
        WorkspaceManifest manifest;
        try
        {
            manifest = WorkspaceManifestFile.Load(_root);
        }
        catch (JsonException ex)
        {
            throw new WorkspaceException(
                $"Workspace manifest at '{SqueezrPaths.ManifestPath(_root)}' is not valid JSON: {ex.Message}");
        }

        if (manifest.RootPath is null)
        {
            WorkspaceManifestFile.Save(_root, manifest with { RootPath = canonicalRoot });
        }
    }

    private static string? FindNameMismatchHint(string adoptedStoreKey, string canonicalRoot)
    {
        var workspacesDir = SqueezrHome.WorkspacesDir;
        if (!Directory.Exists(workspacesDir))
        {
            return null;
        }

        string[] storeDirs;
        try
        {
            storeDirs = Directory.EnumerateDirectories(workspacesDir).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var storeDir in storeDirs)
        {
            var otherKey = Path.GetFileName(storeDir);
            if (string.Equals(otherKey, adoptedStoreKey, StringComparison.Ordinal)
                || WorkspaceRegistry.FindByStoreKey(otherKey) is not null)
            {
                continue;
            }

            var manifest = TryLoadStoreManifest(storeDir);
            if (manifest?.RootPath is not { } rootPath)
            {
                continue;
            }

            if (string.Equals(WorkspaceCanonicalPath.Canonicalize(rootPath), canonicalRoot, PathComparison))
            {
                return $"another workspace store '{otherKey}' also points at '{canonicalRoot}'; run 'squeezr workspace repair' to reconcile it";
            }
        }

        return null;
    }

    private static WorkspaceManifest? TryLoadStoreManifest(string storeDir)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceManifest>(
                File.ReadAllText(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName)),
                SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
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
