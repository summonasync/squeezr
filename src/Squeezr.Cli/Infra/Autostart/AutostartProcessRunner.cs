using System.ComponentModel;
using System.Diagnostics;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Cli.Infra.Autostart;

public sealed class AutostartProcessRunner : IAutostartProcessRunner
{
    internal static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(30);

    public Task<AutostartProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return SpawnAsync(startInfo, cancellationToken);
    }

    public Task<AutostartProcessResult> RunRawAsync(
        string fileName,
        string argumentLine,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(fileName);
        // ProcessStartInfo.Arguments reaches the command line untouched, which is what a line that
        // is already quoted at argv level needs; the ArgumentList path would quote it again.
        startInfo.Arguments = argumentLine;
        return SpawnAsync(startInfo, cancellationToken);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName) => new()
    {
        FileName = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static async Task<AutostartProcessResult> SpawnAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var fileName = startInfo.FileName;
        using var process = Start(fileName, startInfo);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RunBudget);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(budget.Token);
            return new AutostartProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new AutostartException(
                $"'{fileName}' did not finish within {RunBudget.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    // A missing platform tool (no systemctl in a container, no schtasks on a non-Windows box) is an
    // operator-readable refusal, not a stack trace.
    private static Process Start(string fileName, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new AutostartException($"Could not start '{fileName}'.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new AutostartException($"Could not start '{fileName}': {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }
}
