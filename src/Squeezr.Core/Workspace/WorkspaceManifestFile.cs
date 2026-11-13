using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public static class WorkspaceManifestFile
{
    public static bool Exists(string root) => File.Exists(SqueezrPaths.ManifestPath(root));

    public static WorkspaceManifest Load(string root) => LoadAt(ManifestPathOf(root));

    public static void Save(string root, WorkspaceManifest manifest) => SaveAt(ManifestPathOf(root), manifest);

    // A store-to-store pass knows the destination file, not the destination's registry row: the child
    // root may not be discoverable from the process that is filling it.
    public static WorkspaceManifest LoadAt(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<WorkspaceManifest>(json, SqueezrJson.FileOptions)
               ?? throw new WorkspaceException($"Workspace manifest at '{manifestPath}' is corrupt.");
    }

    public static void SaveAt(string manifestPath, WorkspaceManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, SqueezrJson.FileOptions);
        File.WriteAllText(manifestPath, json);
    }

    // A reporting surface asks 'what does this store say about itself' and must not die over the answer: a
    // manifest that will not read is the same no-marker as a manifest that was never written, and the store's
    // own rows stay the subject of the report. Read surfaces never claim a cause they did not measure, so a
    // failed read here is silent by contract, not by accident.
    public static ReconcileMarker? TryRead(string root) => TryReadAt(SqueezrPaths.ManifestPath(root));

    public static ReconcileMarker? TryReadAt(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath) ? LoadAt(manifestPath).Reconcile : null;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    // The marker write is the fill's and the pass's own bookkeeping, so it goes through this door rather
    // than a Load/Save pair at each call site. It is deliberately NOT safe: a fill that could not record
    // that it is about to move bytes must not move them, and a pass that could not record its proof must
    // not claim one. Both callers say so in their own words.
    public static void WriteReconcileAt(string manifestPath, string root, ReconcileMarker marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = File.Exists(manifestPath)
            ? LoadAt(manifestPath)
            : new WorkspaceManifest
            {
                Name = new DirectoryInfo(root).Name,
                RootPath = WorkspaceCanonicalPath.Canonicalize(root),
            };
        SaveAt(manifestPath, manifest with { Reconcile = marker });
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;

    private static string ManifestPathOf(string root) => SqueezrPaths.ManifestPath(root);
}
