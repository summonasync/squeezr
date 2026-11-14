using System.Diagnostics;
using System.Text.RegularExpressions;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests;

internal static partial class CliTestProcess
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    // Captured once on first use so that every child CLI in a test run is pinned
    // to the same central store, even if a ProcessGlobals test (running earlier
    // in the same serialized collection) left SQUEEZR_HOME pointing at its own
    // temp dir and SqueezrHome.Reset() cleared the cache. Without this snapshot,
    // SqueezrHome.Resolve() at Process.Start time could re-read a stale env var
    // and send the child to a registry where the workspace is unknown.
    // The snapshot is the one value a broken neighbour can poison for the whole run, so it is checked on
    // every use instead of trusted for the rest of the process: see RunHome and feature 118 REQ-1.
    private static readonly string TestRunHome = SqueezrHome.Resolve();

    private static string RunHome() => TestRunHomeGuard.RequireContained(TestRunHome, requireAmbientPin: false);

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

    // The BUGS/010 pin harness: the child sees a raw dangling or unset HOME. squeezrHome "" means
    // SQUEEZR_HOME is present but empty, which the product reads as unset; childHome null leaves
    // HOME unset entirely, a path passes it through raw. DOTNET_CLI_HOME keeps the dotnet host
    // itself alive — it refuses to start with no home at all, and the product must see the damage.
    public static Task<(int ExitCode, string StdOut, string StdErr)> RunWithProfileHomeAsync(
        string? childHome, string squeezrHome, string workingDir, params string[] args) =>
        StartAsync(workingDir, args, squeezrHome, childHome: childHome);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> StartAsync(
        string workingDir, string[] args, string? squeezrHome, string? stdin = null, string? childHome = null)
    {
        var startInfo = BuildStartInfo(workingDir, args, squeezrHome, stdin is not null, childHome);
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

    // For the daemon-shaped cases: the caller has to watch the child run, not only await its exit,
    // so the pipes stay open and the caller owns the process. Same isolation as every other child.
    public static Process StartLiveChild(string squeezrHome, string workingDir, params string[] args) =>
        Process.Start(BuildStartInfo(workingDir, args, squeezrHome, redirectStdin: false, childHome: null))
        ?? throw new WorkspaceException("Failed to start the squeezr CLI child process.");

    internal static string CliAssemblyPath => CliDll;

    private static ProcessStartInfo BuildStartInfo(
        string workingDir,
        string[] args,
        string? squeezrHome,
        bool redirectStdin,
        string? childHome)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["SQUEEZR_HOME"] = squeezrHome ?? RunHome();
        // ProcessStartInfo.Environment starts as a copy of ours, so an unset variable
        // here means the child inherits the developer's real store and profile. No test
        // has a reason to touch either, so isolation is the default rather than a request.
        startInfo.Environment["SQUEEZR_CLIENT_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["DOTNET_CLI_HOME"] = TestDirs.SharedClientHomeDir;
        if (childHome is null)
        {
            startInfo.Environment["HOME"] = TestDirs.SharedClientHomeDir;
        }
        else if (childHome.Length > 0)
        {
            startInfo.Environment["HOME"] = childHome;
        }
        else
        {
            startInfo.Environment.Remove("HOME");
        }

        return startInfo;
    }

    [GeneratedRegex("\\u001B\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiSequence();

    private static string StripAnsi(string output) => AnsiSequence().Replace(output, string.Empty);
}
