using System.Diagnostics;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

internal sealed class ZombieHolder : IDisposable
{
    private readonly Process _parent;

    private ZombieHolder(Process parent, int doomedPid)
    {
        _parent = parent;
        ZombiePid = doomedPid;
    }

    internal int ZombiePid { get; }

    // The child must be killed only after the shell is gone: bash collects its pending zombies both while
    // waiting for a foreground command and on the exec path, so the script execs into sleep first and the
    // kill comes from outside. A sleeping parent never calls waitpid, so the zombie holds until the parent
    // is torn down and the orphan lands on init.
    internal static ZombieHolder Spawn()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("sleep 60 & child=$!; echo \"$child\"; exec sleep 60");

        var parent = Process.Start(startInfo)!;
        var childLine = parent.StandardOutput.ReadLine();
        if (!int.TryParse(childLine, out var doomedPid))
        {
            parent.Kill(entireProcessTree: true);
            parent.Dispose();
            throw new InvalidOperationException($"bash reported no child pid, stdout line was '{childLine}'");
        }

        return new ZombieHolder(parent, doomedPid);
    }

    internal void AwaitZombie()
    {
        Process.GetProcessById(ZombiePid).Kill();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (ProcStat.TryReadState(ZombiePid) != ProcStat.ZombieState)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"pid {ZombiePid} never reached zombie state in /proc");
            }

            Thread.Sleep(20);
        }
    }

    public void Dispose()
    {
        try
        {
            _parent.Kill(entireProcessTree: true);
            _parent.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }

        _parent.Dispose();
    }
}
