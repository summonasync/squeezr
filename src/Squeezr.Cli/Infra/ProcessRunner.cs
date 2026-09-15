using System.ComponentModel;
using System.Diagnostics;

namespace Squeezr.Cli.Infra;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(invocation),
            EnableRaisingEvents = true
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(invocation.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainQuietly(stdoutTask, stderrTask);
            throw new TimeoutException($"Process '{invocation.FileName}' timed out after {invocation.Timeout}.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainQuietly(stdoutTask, stderrTask);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessRunResult(process.ExitCode, stdout, stderr);
    }

    // Killed processes can tear the redirected streams, aborting the pending reads;
    // their outcomes are intentionally discarded once the timeout has been reported.
    private static async Task DrainQuietly(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
