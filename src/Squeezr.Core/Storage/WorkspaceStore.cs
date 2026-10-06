using Squeezr.Core.Configuration;
using Squeezr.Core.Infra;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class WorkspaceStore
{
    private const string ClaudeProjectDirEnvironmentVariable = "CLAUDE_PROJECT_DIR";

    public static WorkspaceResolution Resolve(string startDirectory)
    {
        var start = WorkspaceCanonicalPath.Canonicalize(startDirectory);
        var fromStart = ResolveUpward(start);
        if (fromStart.IsWorkspace)
        {
            return fromStart;
        }

        var claudeProjectDir = Environment.GetEnvironmentVariable(ClaudeProjectDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(claudeProjectDir) && Directory.Exists(claudeProjectDir))
        {
            var fromEnv = ResolveUpward(WorkspaceCanonicalPath.Canonicalize(claudeProjectDir));
            if (fromEnv.IsWorkspace)
            {
                return fromEnv with { ViaClaudeProjectDir = true };
            }
        }

        return fromStart;
    }

    public static IReadOnlyList<string> DropByStoreKey(string storeKey, Action<string>? onStaleTakeover = null)
    {
        var storeDir = WorkspaceStorePaths.RequireStoreDir(storeKey);
        var entry = WorkspaceRegistry.FindByStoreKey(storeKey)
                    ?? throw new WorkspaceException($"No workspace is registered with store key '{storeKey}'.");
        var removed = new List<string>();
        if (Directory.Exists(storeDir))
        {
            RemoveStoreDir(storeDir, storeKey, onStaleTakeover);
            removed.Add(storeDir);
        }

        WorkspaceRegistry.RemoveByStoreKey(entry.StoreKey);
        return removed;
    }

    private static void RemoveStoreDir(string storeDir, string storeKey, Action<string>? onStaleTakeover)
    {
        try
        {
            using (IndexLock.Acquire(storeDir, onStaleTakeover))
            {
            }

            Directory.Delete(storeDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceStoreRemovalException(storeKey, ex);
        }
    }

    private static WorkspaceResolution ResolveUpward(string canonicalStart)
    {
        var current = canonicalStart;
        while (true)
        {
            var entry = WorkspaceRegistry.FindByRootPath(current);
            if (entry is not null)
            {
                // A row read off disk can name a store key that is not one segment; then nothing
                // may be resolved or migrated through it, because both would land outside workspaces/.
                var keyIsOneSegment = WorkspaceStorePaths.IsSingleSegmentKey(entry.StoreKey);
                var completedPartialMigration = keyIsOneSegment && CompleteLegacyMigration(entry);
                return new WorkspaceResolution(
                    entry.RootPath,
                    entry.StoreKey,
                    keyIsOneSegment ? StoreDirFor(entry.StoreKey) : null,
                    IsWorkspace: true,
                    completedPartialMigration);
            }

            if (HasLegacyLayout(current))
            {
                var migrated = MigrateLegacyWorkspace(current);
                return new WorkspaceResolution(
                    migrated.RootPath,
                    migrated.StoreKey,
                    StoreDirFor(migrated.StoreKey),
                    IsWorkspace: true,
                    MigratedFromLegacy: true);
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                return new WorkspaceResolution(canonicalStart, StoreKey: null, StoreDir: null, IsWorkspace: false, MigratedFromLegacy: false);
            }

            current = WorkspaceCanonicalPath.Canonicalize(parent.FullName);
        }
    }

    private static bool HasLegacyLayout(string directory)
    {
        if (File.Exists(Path.Combine(directory, SqueezrPaths.ManifestFileName)))
        {
            return true;
        }

        var storeDir = Path.Combine(directory, SqueezrPaths.StoreDirName);
        return Directory.Exists(storeDir) && !SqueezrHome.IsCentralStore(storeDir);
    }

    private static WorkspaceRegistryEntry MigrateLegacyWorkspace(string root)
    {
        RefuseCentralStoreMigration(root);

        var name = ResolveLegacyName(root);
        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var entry = new WorkspaceRegistryEntry(
            name,
            canonicalRoot,
            WorkspaceStoreKeys.Create(name, canonicalRoot),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        WorkspaceRegistry.Add(entry);
        MoveLegacyContent(root, entry.StoreKey);
        return entry;
    }

    private static bool CompleteLegacyMigration(WorkspaceRegistryEntry entry)
    {
        if (!HasLegacyLayout(entry.RootPath))
        {
            return false;
        }

        MoveLegacyContent(entry.RootPath, entry.StoreKey);
        return true;
    }

    private static string ResolveLegacyName(string root)
    {
        var manifestPath = Path.Combine(root, SqueezrPaths.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            return WorkspaceManifestFile.Load(root).Name;
        }

        return new DirectoryInfo(root).Name;
    }

    private static void RefuseCentralStoreMigration(string root)
    {
        var storeDir = Path.Combine(root, SqueezrPaths.StoreDirName);
        if (SqueezrHome.IsCentralStore(storeDir))
        {
            throw new WorkspaceException(
                $"Refusing to migrate '{storeDir}': it is the central squeezr store, not a legacy in-project store.");
        }
    }

    private static void MoveLegacyContent(string root, string storeKey)
    {
        RefuseCentralStoreMigration(root);

        var storeDir = StoreDirFor(storeKey);
        Directory.CreateDirectory(storeDir);

        var legacyStoreDir = Path.Combine(root, SqueezrPaths.StoreDirName);
        if (Directory.Exists(legacyStoreDir))
        {
            foreach (var file in Directory.EnumerateFiles(legacyStoreDir))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, SqueezrPaths.StateFileName, StringComparison.Ordinal))
                {
                    File.Delete(file);
                    continue;
                }

                File.Move(file, Path.Combine(storeDir, fileName), overwrite: true);
            }

            foreach (var dir in Directory.EnumerateDirectories(legacyStoreDir))
            {
                MoveDirectoryContents(dir, Path.Combine(storeDir, Path.GetFileName(dir)));
            }

            Directory.Delete(legacyStoreDir, recursive: true);
        }

        var legacyManifest = Path.Combine(root, SqueezrPaths.ManifestFileName);
        if (File.Exists(legacyManifest))
        {
            File.Move(legacyManifest, Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName), overwrite: true);
        }

        var composePath = Path.Combine(root, SqueezrPaths.ComposeFileName);
        if (ServicesComposeFile.IsSqueezrGenerated(composePath))
        {
            File.Delete(composePath);
        }

        NormalizeMigratedConfig(root);
    }

    private static void NormalizeMigratedConfig(string root)
    {
        SqueezrConfig config;
        try
        {
            config = ConfigFile.Load(root);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        var ignore = config.Indexing.Ignore;
        var missingDefaults = SqueezrConfig.CreateDefault().Indexing.Ignore
            .Where(glob => !ignore.Contains(glob, StringComparer.Ordinal));

        // The save round-trips the loaded document (never CreateDefault), so explicit
        // legacy settings survive; unknown keys such as the removed gitAware flag do not.
        try
        {
            ConfigFile.Save(root, config with
            {
                Indexing = config.Indexing with { Ignore = [.. ignore, .. missingDefaults] }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void MoveDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Move(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            MoveDirectoryContents(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    private static string StoreDirFor(string storeKey) => Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
}
