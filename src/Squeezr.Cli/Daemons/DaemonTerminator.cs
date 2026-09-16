using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Daemons;

public static class DaemonTerminator
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);

    public const string Graceful = "graceful";
    public const string Killed = "killed";

    private const int Sigterm = 15;
    private const int PollIntervalMs = 100;

    public static async Task<string> StopAsync(int pid, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            KillNow(pid);
            return Killed;
        }

        Terminate(pid);
        if (await WaitForExitAsync(pid, GracePeriod, cancellationToken))
        {
            return Graceful;
        }

        KillNow(pid);
        return Killed;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static void Terminate(int pid) => kill(pid, Sigterm);

    private static void KillNow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
        catch (Win32Exception ex)
        {
            throw new WorkspaceException($"Failed to stop pid {pid}: {ex.Message}", innerException: ex);
        }
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (IsRunning(pid))
            {
                await Task.Delay(PollIntervalMs, timeoutCts.Token);
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IsRunning(pid) == false;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
