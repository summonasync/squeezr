using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class DaemonHeartbeatReaderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        DaemonHeartbeatReader.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void TryRead_CorruptJson_ReturnsNull()
    {
        File.WriteAllText(HeartbeatPath(), "not json {{{");

        DaemonHeartbeatReader.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void TryRead_NullLiteral_ReturnsNull()
    {
        File.WriteAllText(HeartbeatPath(), "null");

        DaemonHeartbeatReader.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void TryRead_ValidFile_ReturnsParsedHeartbeat()
    {
        var expected = new DaemonHeartbeat(4242, DaemonHeartbeat.ServeWatchKind, Now, Now + TimeSpan.FromSeconds(5));
        WriteHeartbeat(expected);

        var heartbeat = DaemonHeartbeatReader.TryRead(_storeDir, DaemonHeartbeat.ServeWatchKind);

        heartbeat.ShouldNotBeNull();
        heartbeat.ShouldBe(expected);
        var raw = File.ReadAllText(HeartbeatPath(DaemonHeartbeat.ServeWatchKind));
        raw.ShouldContain("\"pid\"");
        raw.ShouldContain("\"kind\"");
        raw.ShouldContain("\"startedAt\"");
        raw.ShouldContain("\"lastBeatAt\"");
    }

    [Fact]
    public void IsAlive_FreshBeatWithLivePid_ReturnsTrue()
    {
        var heartbeat = new DaemonHeartbeat(1, DaemonHeartbeat.DefaultKind, Now, Now);

        DaemonHeartbeatReader.IsAlive(heartbeat, Now + DaemonHeartbeat.StaleThreshold, _ => true)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsAlive_StaleBeat_ReturnsFalseEvenWhenPidAlive()
    {
        var heartbeat = new DaemonHeartbeat(1, DaemonHeartbeat.DefaultKind, Now, Now);

        DaemonHeartbeatReader.IsAlive(
                heartbeat,
                Now + DaemonHeartbeat.StaleThreshold + TimeSpan.FromTicks(1),
                _ => true)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsAlive_FreshBeatWithDeadPid_ReturnsFalse()
    {
        var heartbeat = new DaemonHeartbeat(1, DaemonHeartbeat.DefaultKind, Now, Now);

        DaemonHeartbeatReader.IsAlive(heartbeat, Now + TimeSpan.FromSeconds(5), _ => false)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsAlive_WithoutSeam_CurrentProcessPid_IsAlive()
    {
        var heartbeat = new DaemonHeartbeat(Environment.ProcessId, DaemonHeartbeat.DefaultKind, Now, Now);

        DaemonHeartbeatReader.IsAlive(heartbeat, Now + TimeSpan.FromSeconds(5))
            .ShouldBeTrue();
    }

    private string HeartbeatPath(string kind = DaemonHeartbeat.DefaultKind) =>
        DaemonHeartbeat.PathFor(_storeDir, kind);

    private void WriteHeartbeat(DaemonHeartbeat heartbeat) =>
        File.WriteAllText(
            HeartbeatPath(heartbeat.Kind),
            JsonSerializer.Serialize(heartbeat, SqueezrJson.FileOptions));

    public void Dispose()
    {
        Directory.Delete(_storeDir, recursive: true);
    }
}
