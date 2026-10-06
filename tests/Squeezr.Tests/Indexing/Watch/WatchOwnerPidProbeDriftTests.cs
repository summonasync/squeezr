using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Watch;

/// <summary>
/// Pid liveness is probed by three separate copies of the same Process.GetProcessById block: DaemonHeartbeatReader,
/// IndexLock and WatchOwnerClaim. Every other test in the slice injects a predicate, so nothing else ever runs the
/// real one. These pins hand both helpers the same real pids and require all three to answer alike: widen or narrow
/// one copy later and the takeover decision stops matching the lock decision.
/// </summary>
public sealed class WatchOwnerPidProbeDriftTests : IDisposable
{
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void PidProbe_LiveOwnerPid_HeartbeatRuleClaimAndLockAllReportItUp()
    {
        var ownerPid = FindLiveForeignPid();
        var now = DateTime.UtcNow;

        DaemonHeartbeatReader.IsAlive(
            new DaemonHeartbeat(ownerPid, DaemonHeartbeat.DefaultKind, now, now), now).ShouldBeTrue();

        WriteOwnerMarker(new WatchOwnerMarker(
            ownerPid,
            Environment.MachineName,
            DaemonHeartbeat.DefaultKind,
            now,
            now - DaemonHeartbeat.StaleThreshold - TimeSpan.FromSeconds(10)));

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerWedged);
        outcome.IsOwner.ShouldBeFalse();
        ReadOwnerMarker()!.Pid.ShouldBe(ownerPid);

        WriteLockFile(ownerPid);
        Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));
    }

    [Fact]
    public void PidProbe_DeadOwnerPid_HeartbeatRuleClaimAndLockAllReportItGone()
    {
        var deadPid = FindDeadPid();
        var now = DateTime.UtcNow;

        DaemonHeartbeatReader.IsAlive(
            new DaemonHeartbeat(deadPid, DaemonHeartbeat.DefaultKind, now, now), now).ShouldBeFalse();

        WriteOwnerMarker(new WatchOwnerMarker(
            deadPid,
            Environment.MachineName,
            DaemonHeartbeat.DefaultKind,
            now - DaemonHeartbeat.StaleThreshold - TimeSpan.FromSeconds(10),
            now - DaemonHeartbeat.StaleThreshold - TimeSpan.FromSeconds(10)));

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.TookOverFrom!.Pid.ShouldBe(deadPid);
        ReadOwnerMarker()!.Pid.ShouldBe(Environment.ProcessId);

        WriteLockFile(deadPid);
        var messages = new List<string>();
        using (IndexLock.Acquire(_storeDir, messages.Add))
        {
            messages.ShouldBe([$"stale lock from pid {deadPid} removed"]);
        }
    }

    [Fact]
    public void PidProbe_MarkerAlreadyNamingThisProcess_IsOwnerWithoutPidEvidence()
    {
        var now = DateTime.UtcNow;
        WriteOwnerMarker(new WatchOwnerMarker(
            Environment.ProcessId,
            Environment.MachineName,
            DaemonHeartbeat.DefaultKind,
            now - TimeSpan.FromMinutes(2),
            now - DaemonHeartbeat.StaleThreshold - TimeSpan.FromSeconds(10)));

        CreateClaim().TryAcquire().Status.ShouldBe(WatchOwnerStatus.Acquired);
    }

    private WatchOwnerClaim CreateClaim() => new(_storeDir, DaemonHeartbeat.DefaultKind);

    private void WriteOwnerMarker(WatchOwnerMarker marker) =>
        File.WriteAllText(
            WatchOwnerMarker.PathFor(_storeDir),
            JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private WatchOwnerMarker? ReadOwnerMarker()
    {
        var path = WatchOwnerMarker.PathFor(_storeDir);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<WatchOwnerMarker>(File.ReadAllText(path), SqueezrJson.FileOptions)
            : null;
    }

    private void WriteLockFile(int pid) =>
        File.WriteAllText(Path.Combine(_storeDir, SqueezrPaths.LockFileName), pid.ToString());

    private static int FindLiveForeignPid() =>
        Environment.ProcessId == 1
            ? Process.GetProcesses().First(candidate => candidate.Id != Environment.ProcessId).Id
            : 1;

    private static int FindDeadPid()
    {
        for (var pid = 999_000; ; pid++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
