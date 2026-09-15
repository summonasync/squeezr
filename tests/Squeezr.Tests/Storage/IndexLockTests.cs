using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

public class IndexLockTests
{
    private readonly string _storeDir;

    public IndexLockTests()
    {
        _storeDir = Path.Combine(TestDirs.NewTempDir(), SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(_storeDir);
    }

    [Fact]
    public void Acquire_FreshLock_GuardsStoreDir_AndDisposeRemovesFile()
    {
        using (var acquired = IndexLock.Acquire(_storeDir))
        {
            File.Exists(LockPath).ShouldBeTrue();
            Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));
        }

        File.Exists(LockPath).ShouldBeFalse();
    }

    [Fact]
    public void Acquire_FreshLock_DoesNotReportStaleTakeover()
    {
        var messages = new List<string>();

        using var acquired = IndexLock.Acquire(_storeDir, messages.Add);

        messages.ShouldBeEmpty();
    }

    [Fact]
    public void Acquire_DeadOwnerPid_TakesOverLock_AndReportsStaleTakeover()
    {
        var deadPid = FindDeadPid();
        File.WriteAllText(LockPath, deadPid.ToString());
        var messages = new List<string>();

        using (var acquired = IndexLock.Acquire(_storeDir, messages.Add))
        {
            Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));
        }

        messages.ShouldBe([$"stale lock from pid {deadPid} removed"]);
    }

    [Fact]
    public void Acquire_EmptyLockFile_FromKilledLegacyProcess_IsTakenOver()
    {
        File.WriteAllText(LockPath, string.Empty);
        var messages = new List<string>();

        using var acquired = IndexLock.Acquire(_storeDir, messages.Add);

        messages.ShouldBe(["stale lock from pid unknown removed"]);
    }

    [Fact]
    public void Acquire_LiveOwnerPid_Refuses()
    {
        File.WriteAllText(LockPath, Environment.ProcessId.ToString());
        var messages = new List<string>();

        Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir, messages.Add));

        messages.ShouldBeEmpty();
        File.ReadAllText(LockPath).ShouldBe(Environment.ProcessId.ToString());
    }

    private string LockPath => Path.Combine(_storeDir, SqueezrPaths.LockFileName);

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
}
