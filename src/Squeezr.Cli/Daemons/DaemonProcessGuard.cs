using System.ComponentModel;
using System.Diagnostics;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Daemons;

public static class DaemonProcessGuard
{
    private static readonly TimeSpan StartedAtTolerance = TimeSpan.FromSeconds(2);

    public static void EnsureSqueezrProcess(DaemonHeartbeat heartbeat)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(heartbeat.Pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw NotSqueezr(heartbeat.Pid);
        }

        using (process)
        {
            if (StartedAtMismatches(process, heartbeat))
            {
                throw new WorkspaceException(
                    $"pid {heartbeat.Pid} was reused by another process (start time mismatch); refusing to stop it.");
            }

            if (!LooksLikeSqueezr(process))
            {
                throw NotSqueezr(heartbeat.Pid);
            }
        }
    }

    public static bool LooksLikeSqueezr(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return LooksLikeSqueezr(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static WorkspaceException NotSqueezr(int pid) =>
        new($"pid {pid} is not a squeezr process; refusing to stop it.");

    private static bool StartedAtMismatches(Process process, DaemonHeartbeat heartbeat)
    {
        try
        {
            var processStartedAtUtc = TruncateToSeconds(process.StartTime.ToUniversalTime());
            var heartbeatStartedAtUtc = TruncateToSeconds(heartbeat.StartedAt);
            return (processStartedAtUtc - heartbeatStartedAtUtc).Duration() > StartedAtTolerance;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    private static bool LooksLikeSqueezr(Process process)
    {
        if (process.ProcessName.Contains("squeezr", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            if (process.MainModule?.FileName.Contains("squeezr", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or FileNotFoundException)
        {
        }

        return !OperatingSystem.IsWindows() && CmdLineContainsSqueezr(process.Id);
    }

    private static bool CmdLineContainsSqueezr(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline")
                .Contains("squeezr", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
