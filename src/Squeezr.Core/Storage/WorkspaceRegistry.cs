using System.Text;
using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class WorkspaceRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, WorkspaceRegistryEntry> Cache = new(PathComparer);
    private static string? _cacheHome;
    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(5);
    private const int WriteLockRetryDelayMs = 25;
    private const int ReadRetryCount = 3;
    private const int ReadRetryDelayMs = 50;
    private const string BackupSuffix = ".bak";

    public static string FilePath => Path.Combine(SqueezrHome.Resolve(), SqueezrPaths.RegistryFileName);

    public static string BackupFilePath => FilePath + BackupSuffix;

    public static IReadOnlyList<WorkspaceRegistryEntry> Load()
    {
        var path = FilePath;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return LoadBackupOrRebuild(path);
                }

                try
                {
                    return JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(File.ReadAllText(path), SqueezrJson.FileOptions)
                           ?? [];
                }
                catch (JsonException)
                {
                    return LoadBackupOrRebuild(path);
                }
            }
            catch (IOException) when (attempt < ReadRetryCount)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }

    public static WorkspaceRegistryEntry? FindByRootPath(string rootPath)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(rootPath);
        lock (Sync)
        {
            InvalidateCacheIfHomeChanged();
            if (Cache.TryGetValue(canonical, out var cached))
            {
                return cached;
            }

            var entry = Load().FirstOrDefault(e => string.Equals(e.RootPath, canonical, Comparison));
            if (entry is not null)
            {
                Cache[canonical] = entry;
            }

            return entry;
        }
    }

    public static WorkspaceRegistryEntry? FindByStoreKey(string storeKey)
    {
        lock (Sync)
        {
            InvalidateCacheIfHomeChanged();
            return Load().FirstOrDefault(e => string.Equals(e.StoreKey, storeKey, StringComparison.Ordinal));
        }
    }

    public static WorkspaceRegistryEntry? FindContainingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
        {
            return null;
        }

        var directory = WorkspaceCanonicalPath.Canonicalize(parent);
        lock (Sync)
        {
            InvalidateCacheIfHomeChanged();
            foreach (var entry in Load())
            {
                var root = entry.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(directory, root, Comparison)
                    || directory.StartsWith(root + Path.DirectorySeparatorChar, Comparison))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    public static void Add(WorkspaceRegistryEntry entry)
    {
        lock (Sync)
        {
            InvalidateCacheIfHomeChanged();
            using var fileLock = AcquireWriteLock();
            var entries = Load().ToList();
            entries.RemoveAll(e => string.Equals(e.RootPath, entry.RootPath, Comparison));
            entries.Add(entry);
            Save(entries);
            Cache[entry.RootPath] = entry;
        }
    }

    public static void RemoveByRoot(string rootPath)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(rootPath);
        lock (Sync)
        {
            InvalidateCacheIfHomeChanged();
            using var fileLock = AcquireWriteLock();
            var entries = Load().ToList();
            entries.RemoveAll(e => string.Equals(e.RootPath, canonical, Comparison));
            Save(entries);
            Cache.Remove(canonical);
        }
    }

    public static void RemoveByStoreKey(string storeKey)
    {
        lock (Sync)
        {
            InvalidateCacheIfHomeChanged();
            using var fileLock = AcquireWriteLock();
            var entries = Load().ToList();
            entries.RemoveAll(e => string.Equals(e.StoreKey, storeKey, StringComparison.Ordinal));
            Save(entries);
            foreach (var key in Cache.Where(pair => pair.Value.StoreKey == storeKey).Select(pair => pair.Key).ToList())
            {
                Cache.Remove(key);
            }
        }
    }

    public static bool TryGetStoreDir(string rootPath, out string storeDir)
    {
        var entry = FindByRootPath(rootPath);
        if (entry is not null)
        {
            storeDir = Path.Combine(SqueezrHome.WorkspacesDir, entry.StoreKey);
            return true;
        }

        storeDir = string.Empty;
        return false;
    }

    private static void Save(IReadOnlyList<WorkspaceRegistryEntry> entries)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateBackup(path);
        OverwriteWithoutRotation(path, entries);
    }

    private static void RotateBackup(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var backupPath = path + BackupSuffix;
        var tempPath = $"{backupPath}.{Guid.NewGuid():N}.tmp";
        File.Copy(path, tempPath);
        File.Move(tempPath, backupPath, overwrite: true);
    }

    private static void OverwriteWithoutRotation(string path, IReadOnlyList<WorkspaceRegistryEntry> entries)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static IReadOnlyList<WorkspaceRegistryEntry> LoadBackupOrRebuild(string path)
    {
        var backupPath = path + BackupSuffix;
        if (File.Exists(backupPath)
            && TryReadEntries(backupPath, out var fromBackup))
        {
            OverwriteWithoutRotation(path, fromBackup);
            return fromBackup;
        }

        return RebuildFromStoreDirs(path);
    }

    private static bool TryReadEntries(string path, out IReadOnlyList<WorkspaceRegistryEntry> entries)
    {
        try
        {
            entries = JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(File.ReadAllText(path), SqueezrJson.FileOptions)
                      ?? [];
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            entries = [];
            return false;
        }
    }

    private static IReadOnlyList<WorkspaceRegistryEntry> RebuildFromStoreDirs(string registryPath)
    {
        var workspacesDir = SqueezrHome.WorkspacesDir;
        string[] storeDirs;
        try
        {
            storeDirs = Directory.Exists(workspacesDir)
                ? Directory.EnumerateDirectories(workspacesDir).ToArray()
                : [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<WorkspaceRegistryEntry>();
        foreach (var storeDir in storeDirs)
        {
            var manifestPath = Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName);
            var manifest = TryReadStoreManifest(manifestPath);
            if (manifest?.RootPath is not { } rootPath || string.IsNullOrWhiteSpace(manifest.Name))
            {
                continue;
            }

            var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(rootPath);
            var storeKey = WorkspaceStoreKeys.Create(manifest.Name, canonicalRoot);
            if (!string.Equals(Path.GetFileName(storeDir), storeKey, StringComparison.Ordinal))
            {
                continue;
            }

            var timestamp = File.GetLastWriteTimeUtc(manifestPath);
            entries.Add(new WorkspaceRegistryEntry(manifest.Name, canonicalRoot, storeKey, timestamp, timestamp));
        }

        if (entries.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        OverwriteWithoutRotation(registryPath, entries);
        return entries;
    }

    private static WorkspaceManifest? TryReadStoreManifest(string manifestPath)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceManifest>(File.ReadAllText(manifestPath), SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void InvalidateCacheIfHomeChanged()
    {
        var home = SqueezrHome.Resolve();
        if (!string.Equals(_cacheHome, home, StringComparison.Ordinal))
        {
            Cache.Clear();
            _cacheHome = home;
        }
    }

    private static string WriteLockPath => Path.Combine(SqueezrHome.Resolve(), SqueezrPaths.RegistryLockFileName);

    // The OS releases a FileShare.None handle when the owning process dies,
    // so a leftover lock file never blocks writers the way a stale marker would.
    private static IDisposable AcquireWriteLock()
    {
        var lockPath = WriteLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow + WriteLockTimeout;
        while (true)
        {
            try
            {
                var stream = new FileStream(WriteLockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                var pid = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                stream.SetLength(0);
                stream.Write(pid, 0, pid.Length);
                stream.Flush();
                return stream;
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw;
                }

                Thread.Sleep(WriteLockRetryDelayMs);
            }
        }
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
