using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutTask = PumpAsync(process.StandardOutput, stdoutBuilder, invocation.ProgressHandler);
        var stderrTask = PumpAsync(process.StandardError, stderrBuilder, invocation.ProgressHandler);
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

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessRunResult(process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder builder, Action<string>? progressHandler)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            builder.Append(buffer, 0, read);
            progressHandler?.Invoke(new string(buffer, 0, read));
        }
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
