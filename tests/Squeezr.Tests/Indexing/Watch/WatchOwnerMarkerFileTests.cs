using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class WatchOwnerMarkerFileTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly string _path;

    public WatchOwnerMarkerFileTests()
    {
        _path = WatchOwnerMarker.PathFor(_storeDir);
    }

    [Fact]
    public void TryDeleteIf_MarkerStillNamesTheEvaluatedOwner_Deletes()
    {
        var dead = Marker(pid: 4100);
        Write(dead);

        WatchOwnerMarkerFile.TryDeleteIf(_path, dead).ShouldBeTrue();

        File.Exists(_path).ShouldBeFalse();
    }

    [Fact]
    public void TryDeleteIf_MarkerChangedHandsUnderUs_RefusesAndKeepsTheNewOwner()
    {
        Write(Marker(pid: 4100));
        var newOwner = Marker(pid: 4200);

        WatchOwnerMarkerFile.TryDeleteIf(_path, newOwner).ShouldBeFalse();

        Read().ShouldBe(Marker(pid: 4100), "the marker evaluated as live must survive a refused reap");
    }

    [Fact]
    public void TryDeleteIf_MarkerAlreadyGone_ReportsNothingToProtect()
    {
        WatchOwnerMarkerFile.TryDeleteIf(_path, Marker(pid: 4100)).ShouldBeTrue();
        File.Exists(_path).ShouldBeFalse();
    }

    [Fact]
    public void TryDeleteIf_SamePidButOlderBeat_Refuses()
    {
        var dead = Marker(pid: 4100);
        Write(dead);
        Write(new WatchOwnerMarker(4100, dead.Host, dead.Kind, dead.StartedAt, dead.LastBeatAt + TimeSpan.FromSeconds(1)));

        WatchOwnerMarkerFile.TryDeleteIf(_path, dead).ShouldBeFalse();
    }

    private static WatchOwnerMarker Marker(int pid) =>
        new(pid, Environment.MachineName, DaemonHeartbeat.DefaultKind, T0, T0);

    private void Write(WatchOwnerMarker marker) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private WatchOwnerMarker? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<WatchOwnerMarker>(File.ReadAllText(_path), SqueezrJson.FileOptions);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
