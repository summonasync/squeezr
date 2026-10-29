using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class SupervisionState
{
    private readonly string _path;

    public SupervisionState() : this(WatchdPaths.StatePath)
    {
    }

    internal SupervisionState(string path) => _path = path;

    public WatchDesire DesireOf(string storeKey) =>
        Load().TryGetValue(storeKey, out var desire) ? desire : WatchDesire.Running;

    public void SetDesire(string storeKey, WatchDesire desire)
    {
        var stores = Load();
        stores[storeKey] = desire;
        Save(stores);
    }

    private Dictionary<string, WatchDesire> Load()
    {
        try
        {
            var file = JsonSerializer.Deserialize<WatchdStateFile>(File.ReadAllText(_path), SqueezrJson.FileOptions);
            return file?.Stores is { } stores
                ? stores.Where(pair => pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => Parse(pair.Value!), StringComparer.Ordinal)
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, WatchDesire> stores)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var file = new WatchdStateFile(
            stores.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal));
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(file, SqueezrJson.FileOptions));
        File.Move(tempPath, _path, overwrite: true);
    }

    private static WatchDesire Parse(string value) =>
        Enum.TryParse<WatchDesire>(value, ignoreCase: true, out var desire) ? desire : WatchDesire.Running;

    private sealed record WatchdStateFile(Dictionary<string, string>? Stores);
}
