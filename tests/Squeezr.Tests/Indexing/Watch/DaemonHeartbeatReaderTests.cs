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

    [Fact]
    public void TryReadAll_NothingWritten_ReturnsEmpty()
    {
        DaemonHeartbeatReader.TryReadAll(_storeDir).ShouldBeEmpty();
    }

    [Fact]
    public void TryReadAll_OldSingleFileBeat_IsReadAsOneProcess()
    {
        WriteHeartbeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now));

        var roster = DaemonHeartbeatReader.TryReadAll(_storeDir);

        roster.ShouldHaveSingleItem().Pid.ShouldBe(4242);
        roster[0].Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        roster[0].Host.ShouldBeNull();
    }

    [Fact]
    public void TryReadAll_PerPidBeatsAndKindHeartbeat_OwnerAnswersOnce()
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        WriteBeatFile(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        WriteBeatFile(new DaemonHeartbeat(4343, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        WriteHeartbeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"));

        var roster = DaemonHeartbeatReader.TryReadAll(_storeDir);

        roster.Select(beat => beat.Pid).ShouldBe([4242, 4343]);
    }

    [Fact]
    public void TryReadAll_BothKinds_KeepTheirOwnProcesses()
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        WriteBeatFile(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        WriteBeatFile(new DaemonHeartbeat(4343, DaemonHeartbeat.ServeWatchKind, Now, Now, "box"));

        DaemonHeartbeatReader.TryReadAll(_storeDir)
            .Select(beat => (beat.Kind, beat.Pid))
            .ShouldBe([
                (DaemonHeartbeat.ServeWatchKind, 4343),
                (DaemonHeartbeat.DefaultKind, 4242),
            ]);
    }

    [Fact]
    public void TryReadAll_BeatContradictingItsNameOrCorrupt_IsSkippedWithoutThrowing()
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        WriteBeatFile(new DaemonHeartbeat(9999, DaemonHeartbeat.DefaultKind, Now, Now, "box"), pidInName: 4242);
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(_storeDir, DaemonHeartbeat.DefaultKind, 5454), "not json {{{");
        File.WriteAllText(Path.Combine(DaemonHeartbeat.BeatsDirFor(_storeDir), "notes.txt"), "stranger");
        WriteBeatFile(new DaemonHeartbeat(4343, DaemonHeartbeat.DefaultKind, Now, Now, "box"));

        var roster = DaemonHeartbeatReader.TryReadAll(_storeDir);

        roster.ShouldHaveSingleItem().Pid.ShouldBe(4343);
    }

    private string WriteBeatFile(DaemonHeartbeat beat, int? pidInName = null)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        var path = DaemonHeartbeat.BeatPathFor(_storeDir, beat.Kind, pidInName ?? beat.Pid);
        File.WriteAllText(path, JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_storeDir, recursive: true);
    }
}
