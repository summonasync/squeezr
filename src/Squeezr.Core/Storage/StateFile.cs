using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class StateFile
{
    public static IndexState Load(string root)
    {
        var json = File.ReadAllText(SqueezrPaths.StatePath(root));
        return JsonSerializer.Deserialize<IndexState>(json, SqueezrJson.FileOptions)
               ?? throw new WorkspaceException($"State file at '{SqueezrPaths.StatePath(root)}' is corrupt.");
    }

    public static void Save(string root, IndexState state)
    {
        var json = JsonSerializer.Serialize(state, SqueezrJson.FileOptions);
        File.WriteAllText(SqueezrPaths.StatePath(root), json);
    }
}
