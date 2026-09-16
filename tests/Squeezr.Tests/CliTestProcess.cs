using System.Diagnostics;

namespace Squeezr.Tests;

internal static class CliTestProcess
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string workingDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = (process.ExitCode, await stdoutTask, await stderrTask);
        if (result.ExitCode == 0 && args.Length > 0 && args[0] == "init")
        {
            // init writes the default config pointing at the developer's real Qdrant;
            // rewire to a dead endpoint so CLI flows degrade deterministically.
            TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workingDir);
        }

        return result;
    }
}
