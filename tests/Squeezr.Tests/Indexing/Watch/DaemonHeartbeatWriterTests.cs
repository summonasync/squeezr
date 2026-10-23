using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class DaemonHeartbeatWriterTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly List<string> _warnings = [];

    [Fact]
    public void Start_CreatesHeartbeatFile_WithCurrentProcessPidKindAndTimestamps()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        using var writer = CreateWriter(utcNow: () => now);

        writer.Start();

        var path = DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind);
        File.Exists(path).ShouldBeTrue();
        var heartbeat = DaemonHeartbeatReader.TryRead(_storeDir);
        heartbeat.ShouldNotBeNull();
        heartbeat.Pid.ShouldBe(Environment.ProcessId);
        heartbeat.Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        heartbeat.StartedAt.ShouldBe(now);
        heartbeat.LastBeatAt.ShouldBe(now);
    }

    [Fact]
    public void Beat_AfterClockAdvance_UpdatesLastBeatAt_KeepsStartedAt()
    {
        var startedAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var now = startedAt;
        using var writer = CreateWriter(utcNow: () => now);
        writer.Start();

        now = startedAt + TimeSpan.FromSeconds(5);
        writer.Beat();

        var heartbeat = DaemonHeartbeatReader.TryRead(_storeDir);
        heartbeat.ShouldNotBeNull();
        heartbeat.StartedAt.ShouldBe(startedAt);
        heartbeat.LastBeatAt.ShouldBe(now);
    }

    [Fact]
    public async Task TimerTick_UpdatesLastBeatAt_WithoutManualBeat()
    {
        using var writer = CreateWriter(beatInterval: TimeSpan.FromMilliseconds(50));
        writer.Start();
        var initial = DaemonHeartbeatReader.TryRead(_storeDir);
        initial.ShouldNotBeNull();

        await UntilAsync(() =>
            DaemonHeartbeatReader.TryRead(_storeDir)?.LastBeatAt > initial.LastBeatAt);

        var heartbeat = DaemonHeartbeatReader.TryRead(_storeDir);
        heartbeat.ShouldNotBeNull();
        heartbeat.StartedAt.ShouldBe(initial.StartedAt);
    }

    [Fact]
    public void Dispose_DeletesHeartbeatFile()
    {
        var writer = CreateWriter();
        writer.Start();
        File.Exists(writer.FilePath).ShouldBeTrue();

        writer.Dispose();

        File.Exists(writer.FilePath).ShouldBeFalse();
    }

    [Fact]
    public void Start_WhenStoreDirMissing_DoesNotThrow_ReportsWarning()
    {
        var missingDir = Path.Combine(TestDirs.NewTempDir(), "does-not-exist");
        using var writer = new DaemonHeartbeatWriter(
            missingDir,
            onWarning: _warnings.Add);

        writer.Start();

        _warnings.ShouldHaveSingleItem().ShouldContain("heartbeat write failed");
    }

    [Fact]
    public void TwoKinds_SameStoreDir_WriteIndependentFiles()
    {
        var watchWriter = new DaemonHeartbeatWriter(_storeDir, DaemonHeartbeat.DefaultKind);
        var serveWriter = new DaemonHeartbeatWriter(_storeDir, DaemonHeartbeat.ServeWatchKind);
        watchWriter.Start();
        serveWriter.Start();

        var watchPath = DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind);
        var servePath = DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.ServeWatchKind);
        File.Exists(watchPath).ShouldBeTrue();
        File.Exists(servePath).ShouldBeTrue();
        DaemonHeartbeatReader.TryRead(_storeDir, DaemonHeartbeat.DefaultKind)!.Kind
            .ShouldBe(DaemonHeartbeat.DefaultKind);
        DaemonHeartbeatReader.TryRead(_storeDir, DaemonHeartbeat.ServeWatchKind)!.Kind
            .ShouldBe(DaemonHeartbeat.ServeWatchKind);

        watchWriter.Dispose();

        File.Exists(watchPath).ShouldBeFalse();
        File.Exists(servePath).ShouldBeTrue();
        serveWriter.Dispose();
        File.Exists(servePath).ShouldBeFalse();
    }

    [Fact]
    public void Start_PublishesItsOwnBeat_BesideTheKindHeartbeat()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        using var writer = CreateWriter(utcNow: () => now);
        writer.Start();

        var beatPath = DaemonHeartbeat.BeatPathFor(_storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);
        writer.BeatPath.ShouldBe(beatPath);
        File.Exists(beatPath).ShouldBeTrue();

        var beat = ReadBeat(beatPath);
        beat.ShouldNotBeNull();
        beat.Pid.ShouldBe(Environment.ProcessId);
        beat.Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        beat.Host.ShouldBe(Environment.MachineName);
        beat.StartedAt.ShouldBe(now);
        File.Exists(DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind)).ShouldBeTrue();
    }

    [Fact]
    public void ObserverPublishesOnlyItsOwnBeat_LeavesTheKindHeartbeatToTheOwner()
    {
        var kindPath = DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind);
        var ownerWrote = new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, DateTime.UtcNow, DateTime.UtcNow);
        File.WriteAllText(kindPath, JsonSerializer.Serialize(ownerWrote, SqueezrJson.FileOptions));

        var writer = new DaemonHeartbeatWriter(_storeDir, DaemonHeartbeat.DefaultKind, publishKindHeartbeat: false);
        writer.Start();

        File.Exists(writer.BeatPath).ShouldBeTrue();
        ReadBeat(kindPath)!.Pid.ShouldBe(4242, "an observer overwrote the owner's kind heartbeat");

        writer.Dispose();

        File.Exists(writer.BeatPath).ShouldBeFalse();
        File.Exists(kindPath).ShouldBeTrue("an observer deleted the kind heartbeat it never owned");
    }

    [Fact]
    public void Beat_CollectsBeatsOfGoneProcesses_KeepsTheOnesStillProvable()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var gone = PlantBeat(4242, now.AddMinutes(-10), now.AddMinutes(-10));
        var wedged = PlantBeat(4343, now.AddHours(-1), now);
        var foreignHost = PlantBeat(4444, now.AddMinutes(-10), now.AddMinutes(-10), host: "elsewhere");

        using var writer = new DaemonHeartbeatWriter(
            _storeDir,
            DaemonHeartbeat.DefaultKind,
            utcNow: () => now,
            isProcessAlive: pid => pid == 4343);
        writer.Start();

        File.Exists(gone).ShouldBeFalse("a beat of a gone process kept the fleet naming a daemon nobody runs");
        File.Exists(wedged).ShouldBeTrue("a stale beat under a live pid is wedge evidence, not garbage");
        File.Exists(foreignHost).ShouldBeTrue("a beat from another host has no pid this process may judge");
    }

    [Fact]
    public void Observer_BeatOfAFailedStoreDir_ReportsThroughTheWarningSinkOnly()
    {
        var missingDir = Path.Combine(TestDirs.NewTempDir(), "does-not-exist");
        var warnings = new List<string>();
        using var writer = new DaemonHeartbeatWriter(
            missingDir,
            DaemonHeartbeat.DefaultKind,
            onWarning: warnings.Add,
            publishKindHeartbeat: false);

        writer.Start();

        warnings.ShouldHaveSingleItem().ShouldContain("heartbeat write failed");
        Directory.Exists(missingDir).ShouldBeFalse("a beat publisher minted a store directory nothing owns");
    }

    [Fact]
    public void StopPublishingKindHeartbeat_DemotedOwner_DropsItsMirror_KeepsBeatingItsOwnBeat()
    {
        var writer = new DaemonHeartbeatWriter(_storeDir, DaemonHeartbeat.DefaultKind, onWarning: _warnings.Add);
        writer.Start();
        File.Exists(writer.FilePath).ShouldBeTrue();

        writer.StopPublishingKindHeartbeat();

        File.Exists(writer.FilePath).ShouldBeFalse(
            "a demoted writer kept signing its name into the per-kind heartbeat it no longer owns");
        File.Exists(writer.BeatPath).ShouldBeTrue(
            "demotion ends the indexer claim, not the process presence: the roster must still name it");

        writer.Beat();
        File.Exists(writer.FilePath).ShouldBeFalse("the kind mirror was re-stamped after demotion");

        writer.Dispose();
        File.Exists(writer.BeatPath).ShouldBeFalse();
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void StopPublishingKindHeartbeat_WhenTheMirrorNamesAnotherWriter_LeavesThatFileAlone()
    {
        var writer = new DaemonHeartbeatWriter(_storeDir, DaemonHeartbeat.DefaultKind);
        writer.Start();

        var newOwner = new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, DateTime.UtcNow, DateTime.UtcNow);
        File.WriteAllText(writer.FilePath, JsonSerializer.Serialize(newOwner, SqueezrJson.FileOptions));

        writer.StopPublishingKindHeartbeat();

        File.Exists(writer.FilePath).ShouldBeTrue("the demotion deleted a mirror that no longer named its writer");
        ReadBeat(writer.FilePath)!.Pid.ShouldBe(4242);

        writer.Dispose();
        File.Exists(writer.FilePath).ShouldBeTrue("a disposed demoted writer erased the live owner's mirror");
        File.Exists(writer.BeatPath).ShouldBeFalse();
    }

    [Fact]
    public void StopPublishingKindHeartbeat_OnAnObserver_PublishesNothingExtra_AndKeepsItsBeat()
    {
        var ownerWrote = new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, DateTime.UtcNow, DateTime.UtcNow);
        var kindPath = DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind);
        File.WriteAllText(kindPath, JsonSerializer.Serialize(ownerWrote, SqueezrJson.FileOptions));
        var writer = new DaemonHeartbeatWriter(_storeDir, DaemonHeartbeat.DefaultKind, publishKindHeartbeat: false);
        writer.Start();

        writer.StopPublishingKindHeartbeat();

        File.Exists(writer.BeatPath).ShouldBeTrue();
        ReadBeat(kindPath)!.Pid.ShouldBe(4242);
        writer.Dispose();
    }

    private string PlantBeat(
        int pid, DateTime startedAt, DateTime lastBeatAt, string? host = null)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        var path = DaemonHeartbeat.BeatPathFor(_storeDir, DaemonHeartbeat.DefaultKind, pid);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new DaemonHeartbeat(pid, DaemonHeartbeat.DefaultKind, startedAt, lastBeatAt, host),
                SqueezrJson.FileOptions));
        return path;
    }

    private static DaemonHeartbeat? ReadBeat(string path) =>
        JsonSerializer.Deserialize<DaemonHeartbeat>(File.ReadAllText(path), SqueezrJson.FileOptions);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        condition().ShouldBeTrue($"condition not met within {WaitTimeout.TotalSeconds}s");
    }

    private DaemonHeartbeatWriter CreateWriter(
        Func<DateTime>? utcNow = null,
        TimeSpan? beatInterval = null) =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, utcNow, beatInterval, _warnings.Add);

    public void Dispose()
    {
        Directory.Delete(_storeDir, recursive: true);
    }
}
