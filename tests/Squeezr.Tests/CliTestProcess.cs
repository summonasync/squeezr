using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Squeezr.Tests;

internal static partial class CliTestProcess
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string workingDir, params string[] args)
    {
        var result = await StartAsync(workingDir, args, squeezrHome: null);
        if (result.ExitCode == 0 && args.Length > 0 && args[0] == "init")
        {
            // init writes the default config pointing at the developer's real Qdrant;
            // rewire to a dead endpoint so CLI flows degrade deterministically.
            TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workingDir);
        }

        return result;
    }

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunWithHomeAsync(
        string squeezrHome, string workingDir, params string[] args) =>
        StartAsync(workingDir, args, squeezrHome);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunPipedAsync(
        string squeezrHome, string stdin, string workingDir, params string[] args) =>
        StartAsync(workingDir, args, squeezrHome, stdin);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> StartAsync(
        string workingDir, string[] args, string? squeezrHome, string? stdin = null)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["NO_COLOR"] = "1";
        if (squeezrHome is not null)
        {
            startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;
        }

        using var process = Process.Start(startInfo)!;
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, StripAnsi(await stdoutTask), StripAnsi(await stderrTask));
    }

    [GeneratedRegex("\\u001B\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiSequence();

    private static string StripAnsi(string output) => AnsiSequence().Replace(output, string.Empty);
}
