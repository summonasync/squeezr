using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// Two holder shapes live in this file and they pin different things. The synthetic ones write the pid
// into index.lock without ever opening a handle, so they pin how the envelope is rendered once the
// owner is known. The held ones acquire a real IndexLock in this very process: the holder's handle stays
// open for the whole block, which is the incident shape, and it is the only shape that can tell an
// honest report from a report that quietly degrades because it could not read the lock.
public class IndexLockHolderErrorTests
{
    private readonly string _storeDir;

    public IndexLockHolderErrorTests()
    {
        _storeDir = Path.Combine(TestDirs.NewTempDir(), SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(_storeDir);
    }

    [Fact]
    public void LiveHolder_ErrorNamesPid_LockAge_AndConcreteNextCommand()
    {
        File.WriteAllText(LockPath, Environment.ProcessId.ToString());
        File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow - TimeSpan.FromSeconds(90));

        var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

        ex.Code.ShouldBe(WorkspaceLockedException.LockedCode);
        ex.Message.ShouldContain($"pid {Environment.ProcessId}");
        ex.Message.ShouldContain("90s");
        ex.Message.ShouldContain("squeezr fleet stop");
        ex.Message.ShouldContain("Do not delete index.lock");
    }

    [Fact]
    public void LiveHolder_WithHeartbeatAndStuckPass_ReportsBothSignals()
    {
        var pid = Environment.ProcessId;
        File.WriteAllText(LockPath, pid.ToString());
        var now = DateTime.UtcNow;
        File.WriteAllText(
            DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind),
            JsonSerializer.Serialize(
                new DaemonHeartbeat(pid, DaemonHeartbeat.DefaultKind, now.AddHours(-2), now.AddSeconds(-4)),
                SqueezrJson.FileOptions));
        var wedgeStart = now.AddMinutes(-40);
        PassPhaseStampFile.Write(
            _storeDir,
            new PassPhaseStamp(pid, PassPhaseStamp.StuckPhase, wedgeStart, wedgeStart, 3));

        var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

        ex.Message.ShouldContain("heartbeat 'watch'");
        ex.Message.ShouldContain("STUCK");
        ex.Message.ShouldContain("alive-but-wedged");
    }

    [Fact]
    public void LiveHolder_WithRunningPass_SaysPassNotFinished()
    {
        var pid = Environment.ProcessId;
        File.WriteAllText(LockPath, pid.ToString());
        PassPhaseStampFile.Write(
            _storeDir,
            new PassPhaseStamp(pid, PassPhaseStamp.RunningPhase, DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow.AddSeconds(-30), 5));

        var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

        ex.Message.ShouldContain("has not finished");
        ex.Message.ShouldNotContain("STUCK");
    }

    [Fact]
    public void LiveHolder_WithoutProgressFiles_StillNamesPidAndAge()
    {
        File.WriteAllText(LockPath, Environment.ProcessId.ToString());

        var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

        ex.Message.ShouldContain($"pid {Environment.ProcessId}");
        ex.Message.ShouldContain("No holder heartbeat");
    }

    [Fact]
    public void DeadHolder_StillTakenOver_StaysQuiet()
    {
        var deadPid = FindDeadPid();
        File.WriteAllText(LockPath, deadPid.ToString());
        var messages = new List<string>();

        using var acquired = IndexLock.Acquire(_storeDir, messages.Add);

        acquired.ShouldNotBeNull();
        messages.ShouldNotBeEmpty();
    }

    [LinuxFact]
    public void HeldLock_ErrorNamesPidAndLockAge_NotTheUnreadableBranch()
    {
        using (IndexLock.Acquire(_storeDir))
        {
            File.Exists(LockPath).ShouldBeTrue("the holder never reached the lock file");
            File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow - TimeSpan.FromSeconds(90));

            var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

            ex.Code.ShouldBe(WorkspaceLockedException.LockedCode);
            ex.Message.ShouldNotContain("cannot be read");
            ex.Message.ShouldContain($"pid {Environment.ProcessId}");
            ex.Message.ShouldContain("lock held for 90s");
        }
    }

    [LinuxFact]
    public void HeldLock_WithHeartbeatAndStuckPass_ReportsWedgedShape()
    {
        using (IndexLock.Acquire(_storeDir))
        {
            var pid = Environment.ProcessId;
            var now = DateTime.UtcNow;
            File.WriteAllText(
                DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind),
                JsonSerializer.Serialize(
                    new DaemonHeartbeat(pid, DaemonHeartbeat.DefaultKind, now.AddHours(-2), now.AddSeconds(-4)),
                    SqueezrJson.FileOptions));
            var wedgeStart = now.AddMinutes(-40);
            PassPhaseStampFile.Write(
                _storeDir,
                new PassPhaseStamp(pid, PassPhaseStamp.StuckPhase, wedgeStart, wedgeStart, 3));

            var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

            ex.Message.ShouldNotContain("cannot be read");
            ex.Message.ShouldContain($"pid {pid}");
            ex.Message.ShouldContain("heartbeat 'watch'");
            ex.Message.ShouldContain("STUCK");
            ex.Message.ShouldContain("alive-but-wedged");
        }
    }

    [LinuxFact]
    public void HeldLock_WithRunningPass_SaysPassNotFinished()
    {
        using (IndexLock.Acquire(_storeDir))
        {
            var pid = Environment.ProcessId;
            PassPhaseStampFile.Write(
                _storeDir,
                new PassPhaseStamp(pid, PassPhaseStamp.RunningPhase, DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow.AddSeconds(-30), 5));

            var ex = Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));

            ex.Message.ShouldNotContain("cannot be read");
            ex.Message.ShouldContain("has not finished");
            ex.Message.ShouldNotContain("STUCK");
        }
    }

    private string LockPath => Path.Combine(_storeDir, SqueezrPaths.LockFileName);

    private static int FindDeadPid()
    {
        for (var pid = 999_000; ; pid++)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }
}
