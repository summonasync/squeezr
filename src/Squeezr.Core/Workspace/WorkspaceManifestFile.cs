using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

public static class WorkspaceManifestFile
{
    public static bool Exists(string root) => File.Exists(SqueezrPaths.ManifestPath(root));

    public static WorkspaceManifest Load(string root)
    {
        var json = File.ReadAllText(SqueezrPaths.ManifestPath(root));
        return JsonSerializer.Deserialize<WorkspaceManifest>(json, SqueezrJson.FileOptions)
               ?? throw new WorkspaceException($"Workspace manifest at '{root}' is corrupt.");
    }

    public static void Save(string root, WorkspaceManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, SqueezrJson.FileOptions);
        File.WriteAllText(SqueezrPaths.ManifestPath(root), json);
    }
}
