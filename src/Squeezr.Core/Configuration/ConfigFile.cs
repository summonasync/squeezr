using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Configuration;

public static class ConfigFile
{
    public static SqueezrConfig Load(string root)
    {
        var json = File.ReadAllText(SqueezrPaths.ConfigPath(root));
        return JsonSerializer.Deserialize<SqueezrConfig>(json, SqueezrJson.FileOptions)
               ?? throw new WorkspaceException($"Config file at '{SqueezrPaths.ConfigPath(root)}' is corrupt.");
    }

    public static void Save(string root, SqueezrConfig config)
    {
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        var json = JsonSerializer.Serialize(config, SqueezrJson.FileOptions);
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
    }
}
