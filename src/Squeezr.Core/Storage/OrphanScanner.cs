using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class OrphanScanner
{
    public static OrphanScanResult Scan()
    {
        var entries = WorkspaceRegistry.Load();
        var registeredKeys = entries.Select(entry => entry.StoreKey).ToHashSet(StringComparer.Ordinal);

        var orphans = new List<OrphanInfo>();
        foreach (var storeDir in EnumerateStoreDirs())
        {
            var storeKey = Path.GetFileName(storeDir);
            if (registeredKeys.Contains(storeKey))
            {
                continue;
            }

            var manifest = TryLoadStoreManifest(storeDir);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name))
            {
                continue;
            }

            if (manifest.RootPath is { } rootPath)
            {
                var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(rootPath);
                if (!string.Equals(WorkspaceStoreKeys.Create(manifest.Name, canonicalRoot), storeKey, StringComparison.Ordinal))
                {
                    continue;
                }

                orphans.Add(new OrphanInfo(storeKey, manifest.Name, canonicalRoot));
            }
            else
            {
                orphans.Add(new OrphanInfo(storeKey, manifest.Name, null));
            }
        }

        var husks = entries.Where(entry => IsHusk(entry.RootPath))
            .Select(entry => new HuskInfo(entry.Name, entry.StoreKey, entry.RootPath))
            .ToList();

        return new OrphanScanResult(orphans, husks);
    }

    internal static WorkspaceManifest? TryLoadStoreManifest(string storeDir)
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

    private static bool IsHusk(string rootPath)
    {
        var root = WorkspaceCanonicalPath.Canonicalize(rootPath);
        var userHome = WorkspaceCanonicalPath.Canonicalize(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return string.Equals(root, userHome, PathComparison)
               || string.Equals(root, WorkspaceCanonicalPath.Canonicalize(SqueezrHome.Resolve()), PathComparison);
    }

    private static string[] EnumerateStoreDirs()
    {
        var workspacesDir = SqueezrHome.WorkspacesDir;
        try
        {
            return Directory.Exists(workspacesDir)
                ? Directory.EnumerateDirectories(workspacesDir).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
