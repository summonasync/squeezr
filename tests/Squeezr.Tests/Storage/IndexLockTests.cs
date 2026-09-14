using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("CliProcessGlobals")]
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
    public void Acquire_DeadOwnerPid_TakesOverLock_AndWarnsOnStderr()
    {
        var deadPid = FindDeadPid();
        File.WriteAllText(LockPath, deadPid.ToString());

        var stderr = CaptureStderr(() =>
        {
            using var acquired = IndexLock.Acquire(_storeDir);
            Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));
        });

        stderr.ShouldContain($"warning: stale lock from pid {deadPid} removed");
    }

    [Fact]
    public void Acquire_EmptyLockFile_FromKilledLegacyProcess_IsTakenOver()
    {
        File.WriteAllText(LockPath, string.Empty);

        var stderr = CaptureStderr(() =>
        {
            using var acquired = IndexLock.Acquire(_storeDir);
            Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));
        });

        stderr.ShouldContain("warning: stale lock from pid unknown removed");
    }

    [Fact]
    public void Acquire_LiveOwnerPid_Refuses()
    {
        File.WriteAllText(LockPath, Environment.ProcessId.ToString());

        Should.Throw<WorkspaceLockedException>(() => IndexLock.Acquire(_storeDir));
        File.ReadAllText(LockPath).ShouldBe(Environment.ProcessId.ToString());
    }

    private string LockPath => Path.Combine(_storeDir, SqueezrPaths.LockFileName);

    private static string CaptureStderr(Action action)
    {
        var captured = new StringWriter();
        var original = Console.Error;
        Console.SetError(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
    }

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
