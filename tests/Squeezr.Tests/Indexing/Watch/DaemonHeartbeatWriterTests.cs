using Shouldly;
using Squeezr.Core.Indexing.Watch;

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
