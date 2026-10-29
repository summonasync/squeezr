using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Tui;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Tui;

public sealed class WatchdDaemonProbeTests : IDisposable
{
    private readonly string _dir = TestDirs.NewTempDir();

    [Fact]
    public void MissingLockFile_NotRunning()
    {
        new WatchdDaemonProbe(LockPath).Inspect().Running.ShouldBeFalse();
    }

    [Fact]
    public void LiveHolderOnThisHost_RunningWithPid()
    {
        WriteLock(Environment.MachineName);
        var status = new WatchdDaemonProbe(LockPath, _ => true).Inspect();

        status.Running.ShouldBeTrue();
        status.OwnerPid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void DeadHolderOnThisHost_NotRunning()
    {
        WriteLock(Environment.MachineName);

        new WatchdDaemonProbe(LockPath, _ => false).Inspect().Running.ShouldBeFalse();
    }

    [Fact]
    public void HolderOnAnotherHost_CountsAsRunning()
    {
        WriteLock("some-other-box");

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

    private void WriteLock(string host)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        var owner = new { Pid = Environment.ProcessId, Host = host, StartedAtUtc = DateTime.UtcNow };
        File.WriteAllText(LockPath, JsonSerializer.Serialize(owner, SqueezrJson.FileOptions));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
