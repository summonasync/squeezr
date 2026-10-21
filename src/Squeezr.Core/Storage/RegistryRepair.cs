using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class RegistryRepair
{
    public static RepairOutcome AdoptAll()
    {
        var scan = OrphanScanner.Scan();
        var adopted = new List<OrphanInfo>();
        var needsRoot = new List<OrphanInfo>();
        var failed = new List<OrphanAdoptionFailure>();
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var orphan in scan.Orphans)
        {
            if (orphan is not { IsAdoptable: true, RootPath: { } rootPath })
            {
                needsRoot.Add(orphan);
                continue;
            }

            try
            {
                WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
                    orphan.Name,
                    rootPath,
                    orphan.StoreKey,
                    timestamp,
                    timestamp));
                adopted.Add(orphan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unwritable home fails every row; stopping at the first would report the rest as
                // not attempted, which reads like they were fine.
                failed.Add(new OrphanAdoptionFailure(orphan, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return new RepairOutcome(adopted, needsRoot, scan.Husks, scan.Stale, failed);
    }

    public static WorkspaceRegistryEntry AdoptWithRoot(string storeKey, string rootPath)
    {
        var storeDir = WorkspaceStorePaths.RequireStoreDir(storeKey);
        var manifest = OrphanScanner.TryLoadStoreManifest(storeDir)
                       ?? throw new WorkspaceException(
                           $"Workspace store '{storeKey}' is missing or its manifest is not valid JSON.");
        if (manifest.RootPath is not null)
        {
            throw new WorkspaceException(
                $"Workspace store '{storeKey}' already records root path '{manifest.RootPath}'; omit --root.");
        }

        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(rootPath);
        if (!string.Equals(WorkspaceStoreKeys.Create(manifest.Name, canonicalRoot), storeKey, StringComparison.Ordinal))
        {
            throw new WorkspaceException($"Root path '{rootPath}' does not match store key '{storeKey}'.");
        }

        // The explicit act has no scan snapshot behind it, so liveness is probed here, at the moment the
        // row is written: a row naming a directory that is already gone would be stale on the next scan.
        if (!Directory.Exists(canonicalRoot))
        {
            throw new WorkspaceException(
                $"Root path '{canonicalRoot}' does not exist; adoption requires a live root.");
        }

        var filled = manifest with { RootPath = canonicalRoot };
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(filled, SqueezrJson.FileOptions));

        var entry = new WorkspaceRegistryEntry(
            manifest.Name,
            canonicalRoot,
            storeKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        WorkspaceRegistry.Add(entry);
        return entry;
    }
}
