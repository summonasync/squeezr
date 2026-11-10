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

    private static string ManifestPathOf(string root) => SqueezrPaths.ManifestPath(root);
}
