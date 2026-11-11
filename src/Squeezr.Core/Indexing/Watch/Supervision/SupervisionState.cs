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

    public WatchDesire DesireOf(string storeKey)
    {
        var snapshot = Read();
        return snapshot.Status == SupervisionStateStatus.Unreadable
            ? WatchDesire.Running
            : snapshot.DesireOf(storeKey);
    }

    public void SetDesire(string storeKey, WatchDesire desire)
    {
        var snapshot = ReadForWrite();
        var desires = new Dictionary<string, WatchDesire>(snapshot.Desires, StringComparer.Ordinal);
        desires[storeKey] = desire;
        Save(desires, snapshot.Wedged);
    }

    public SupervisionSnapshot Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SupervisionSnapshot(
                SupervisionStateStatus.Absent,
                _path,
                new Dictionary<string, WatchDesire>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable($"could not be read ({ex.Message})");
        }

        WatchdStateFile? file;
        try
        {
            file = JsonSerializer.Deserialize<WatchdStateFile>(text, SqueezrJson.FileOptions);
        }
        catch (JsonException ex)
        {
            return Unreadable($"is not valid JSON ({ex.Message})");
        }

        var desires = new Dictionary<string, WatchDesire>(StringComparer.Ordinal);
        foreach (var pair in file?.Stores ?? [])
        {
            if (pair.Value is { } value)
            {
                desires[pair.Key] = Parse(value);
            }
        }

        var wedged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var storeKey in file?.Wedged ?? [])
        {
            wedged.Add(storeKey);
        }

        return new SupervisionSnapshot(SupervisionStateStatus.Valid, _path, desires, wedged);
    }

    public void SetWedged(string storeKey, bool wedged)
    {
        var snapshot = ReadForWrite();
        if (snapshot.IsWedged(storeKey) == wedged)
        {
            return;
        }

        var markers = new HashSet<string>(snapshot.Wedged, StringComparer.Ordinal);
        if (wedged)
        {
            markers.Add(storeKey);
        }
        else
        {
            markers.Remove(storeKey);
        }

        Save(snapshot.Desires, markers);
    }

    private SupervisionSnapshot ReadForWrite()
    {
        var snapshot = Read();
        return snapshot.Status == SupervisionStateStatus.Unreadable
            ? throw new SupervisionStateUnreadableException(snapshot)
            : snapshot;
    }

    private SupervisionSnapshot Unreadable(string fault) =>
        new(
            SupervisionStateStatus.Unreadable,
            _path,
            new Dictionary<string, WatchDesire>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            fault);

    private void Save(IReadOnlyDictionary<string, WatchDesire> stores, IReadOnlySet<string> wedged)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var file = new WatchdStateFile(
            stores.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
            wedged.Count == 0 ? null : wedged.OrderBy(storeKey => storeKey, StringComparer.Ordinal).ToArray());
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(file, SqueezrJson.FileOptions));
        File.Move(tempPath, _path, overwrite: true);
    }

    private static WatchDesire Parse(string value) =>
        Enum.TryParse<WatchDesire>(value, ignoreCase: true, out var desire) ? desire : WatchDesire.Running;

    // Wedged is the additive per-store marker of 113/R1: a file written before it stays readable (the
    // property is simply absent), and a file carrying it stays readable by older binaries, which ignore
    // unknown properties. It is null rather than empty when nothing is wedged, so a clean fleet writes
    // byte-for-byte the pre-113 shape.
    private sealed record WatchdStateFile(Dictionary<string, string>? Stores, string[]? Wedged);
}
