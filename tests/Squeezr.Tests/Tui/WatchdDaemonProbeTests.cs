using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Tui;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Tui;

public sealed class WatchdDaemonProbeTests : IDisposable
{
    private readonly string _dir = TestDirs.NewTempDir();

    private static readonly DateTime OwnStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    [Fact]
    public void MissingLockFile_NotRunning()
    {
        new WatchdDaemonProbe(LockPath).Inspect().Running.ShouldBeFalse();
    }

    [Fact]
    public void LiveHolderOnThisHost_RunningWithPid()
    {
        WriteLock(Environment.MachineName, OwnStartUtc);

        var status = new WatchdDaemonProbe(LockPath, _ => true).Inspect();

        status.Running.ShouldBeTrue();
        status.OwnerPid.ShouldBe(Environment.ProcessId);
    }

    // A supervisor that came up seconds ago answers the drift question with a different clock reading than the
    // one it wrote, and the panel must not report it dead for that.
    [Fact]
    public void HolderStartedASecondAgo_IsFreshNotDead()
    {
        WriteLock(Environment.MachineName, OwnStartUtc.AddSeconds(1));

        new WatchdDaemonProbe(LockPath, _ => true).Inspect().Running.ShouldBeTrue();
    }

    [Fact]
    public void HolderOnAReusedPid_IsNotRunning()
    {
        WriteLock(Environment.MachineName, OwnStartUtc.AddHours(-3));

        new WatchdDaemonProbe(LockPath, _ => true).Inspect().Running.ShouldBeFalse();
    }

    [Fact]
    public void HolderWhoseLiveStartCannotBeRead_IsNotRunning()
    {
        WriteLock(Environment.MachineName, OwnStartUtc);

        new WatchdDaemonProbe(LockPath, _ => true, _ => null).Inspect().Running.ShouldBeFalse();
    }

    [Fact]
    public void HolderWithoutARecordedStartTime_LivePidIsEnough()
    {
        WriteLock(Environment.MachineName, startedAtUtc: null);

        var status = new WatchdDaemonProbe(LockPath, _ => true).Inspect();

        status.Running.ShouldBeTrue();
        status.OwnerPid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void DeadHolderOnThisHost_NotRunning()
    {
        WriteLock(Environment.MachineName, OwnStartUtc);

        new WatchdDaemonProbe(LockPath, _ => false).Inspect().Running.ShouldBeFalse();
    }

    [Fact]
    public void HolderOnAnotherHost_CountsAsRunning()
    {
        WriteLock("some-other-box", OwnStartUtc.AddHours(-5));

        new WatchdDaemonProbe(LockPath, _ => false).Inspect().Running.ShouldBeTrue();
    }

    [Fact]
    public void CorruptLockFile_NotRunning()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, "{ not json at all");

        new WatchdDaemonProbe(LockPath, _ => true).Inspect().Running.ShouldBeFalse();
    }

    private string LockPath => Path.Combine(_dir, "watchd", "watchd.lock");

    private void WriteLock(string host, DateTime? startedAtUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        var owner = new
        {
            pid = Environment.ProcessId,
            host,
            startedAtUtc = startedAtUtc?.ToString("O"),
        };
        File.WriteAllText(LockPath, JsonSerializer.Serialize(owner, SqueezrJson.FileOptions));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
