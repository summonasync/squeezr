using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

public sealed class FleetWatchdProbeTests : IDisposable
{
    private readonly string _lockPath = Path.Combine(TestDirs.NewTempDir(), "watchd.lock");

    [Fact]
    public void MissingLockFile_TheSupervisorIsNotRunning()
    {
        FleetWatchdProbe.Probe(_lockPath, _ => true).ShouldBe(new FleetWatchdState(false, null));
    }

    [Fact]
    public void ForeignHostOwner_RunsSomewhereThisMachineCannotCheck()
    {
        WriteLock(1234, "elsewhere", DateTime.UtcNow);

        FleetWatchdProbe.Probe(_lockPath, _ => false).ShouldBe(new FleetWatchdState(true, 1234));
    }

    [Fact]
    public void DeadLocalPid_TheLockIsStale_NotRunning()
    {
        WriteLock(int.MaxValue, Environment.MachineName, DateTime.UtcNow);

        FleetWatchdProbe.Probe(_lockPath, _ => false).ShouldBe(new FleetWatchdState(false, null));
    }

    [Fact]
    public void LiveLocalPid_WithinTheStartTimeTolerance_IsRunning()
    {
        var startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        WriteLock(Environment.ProcessId, Environment.MachineName, startedAt);

        FleetWatchdProbe.Probe(_lockPath, _ => true).ShouldBe(new FleetWatchdState(true, Environment.ProcessId));
    }

    [Fact]
    public void ReusedPid_WhoseStartTimeDrifted_IsNotRunning()
    {
        WriteLock(Environment.ProcessId, Environment.MachineName, DateTime.UtcNow.AddHours(-3));

        FleetWatchdProbe.Probe(_lockPath, _ => true).ShouldBe(new FleetWatchdState(false, null));
    }

    [Fact]
    public void CorruptLockFile_TheSupervisorIsNotRunning()
    {
        File.WriteAllText(_lockPath, "{not json");

        FleetWatchdProbe.Probe(_lockPath, _ => true).ShouldBe(new FleetWatchdState(false, null));
    }

    private void WriteLock(int pid, string host, DateTime startedAtUtc) =>
        File.WriteAllText(
            _lockPath,
            JsonSerializer.Serialize(new { pid, host, startedAtUtc }, SqueezrJson.FileOptions));

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_lockPath)))
        {
            Directory.Delete(Path.GetDirectoryName(_lockPath)!, recursive: true);
        }
    }
}
