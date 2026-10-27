using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

public static class WatchAdoptionEventFile
{
    public static void Write(string storeDir, WatchAdoptionEvent adopted)
    {
        var path = WatchAdoptionEvent.PathFor(storeDir);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(adopted, SqueezrJson.FileOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static WatchAdoptionEvent? TryRead(string storeDir)
    {
        try
        {
            var json = File.ReadAllText(WatchAdoptionEvent.PathFor(storeDir));
            return JsonSerializer.Deserialize<WatchAdoptionEvent>(json, SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
