using System.Text;
using System.Text.Json;
using Squeezr.Core.Serialization;

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

    public static string FilePath => Path.Combine(SqueezrHome.Resolve(), SqueezrPaths.RegistryFileName);

    public static IReadOnlyList<WorkspaceRegistryEntry> Load()
    {
        var path = FilePath;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return [];
                }

                return JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(File.ReadAllText(path), SqueezrJson.FileOptions)
                       ?? [];
            }
            catch (JsonException)
            {
                return [];
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
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));
        File.Move(tempPath, path, overwrite: true);
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
