using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Squeezr.Tests;

internal static partial class CliTestProcess
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
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = (process.ExitCode, StripAnsi(await stdoutTask), StripAnsi(await stderrTask));
        if (result.ExitCode == 0 && args.Length > 0 && args[0] == "init")
        {
            // init writes the default config pointing at the developer's real Qdrant;
            // rewire to a dead endpoint so CLI flows degrade deterministically.
            TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workingDir);
        }

        return result;
    }

    [GeneratedRegex("\\u001B\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiSequence();

    private static string StripAnsi(string output) => AnsiSequence().Replace(output, string.Empty);
}
