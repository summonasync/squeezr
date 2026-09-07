using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Configuration;

public static class ConfigFile
{
    public static SqueezrConfig Load(string root)
    {
        var path = SqueezrPaths.ConfigPath(root);
        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<SqueezrConfig>(json, SqueezrJson.FileOptions)
                   ?? throw new WorkspaceException($"Config file at '{path}' is corrupt.");
        }
        catch (JsonException ex)
        {
            throw new WorkspaceException($"Config file at '{path}' is not valid JSON: {ex.Message}");
        }
    }

    public static void Save(string root, SqueezrConfig config)
    {
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        var json = JsonSerializer.Serialize(config, SqueezrJson.FileOptions);
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
    }
}
