using System.Diagnostics;
using System.Text.Json;
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
    // The snapshot is the one value a broken neighbour can poison for the whole run, so it carries its own
    // provenance and is checked on every use instead of trusted for the rest of the process: a store the
    // run froze out of the profile fallback is refused before the first child spawns (feature 118 REQ-1).
    private static readonly (string Home, bool FromPin) RunStore = CaptureRunStore();

    private static (string Home, bool FromPin) CaptureRunStore()
    {
        // The product answers with the pair, under its own lock. Reading the environment here and the cache
        // there was the earlier shape, and it could return a combination that never existed: a neighbour's
        // pin with this run's fallback value, or a healthy pinned value reported as a fallback and refused
        // for the rest of the run.
        var (home, fromDefault) = SqueezrHome.ResolveWithProvenance();
        return (home, !fromDefault);
    }

    private static string RunHome() =>
        TestRunHomeGuard.RequirePinnedSnapshot(RunStore.Home, RunStore.FromPin);

    // Taken by the module initializer, before any test can hold the pin: touching RunStore here is what
    // makes the snapshot deterministic. Without it the static initializes at whichever test reaches a CLI
    // child first, which may be mid-window while a ProcessGlobals fact owns SQUEEZR_HOME for its own
    // scratch dir - and the alternative, catching that after the fact, fails every later spawn in the run.
    internal static void PrimeRunStore() => _ = RunStore;

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string workingDir, params string[] args)
    {
        var result = await StartAsync(workingDir, args, squeezrHome: null);
        // A verb can exit 0 having created nothing — worktree init reports an unindexed main worktree
        // that way — so the rewire asks the store whether a config is there instead of trusting the code.
        // The config lives at the root the report names, which is not the cwd when the verb took an
        // explicit path (`worktree init <path>`), and a bare `worktree` mints through its default command.
        if (result.ExitCode == 0 && MintsAStore(args))
        {
            var mintedRoot = MintedRootFrom(result.StdOut) ?? workingDir;
            if (File.Exists(SqueezrPaths.ConfigPath(mintedRoot)))
            {
                // init writes the default config pointing at the developer's real Qdrant;
                // rewire to a dead endpoint so CLI flows degrade deterministically.
                TestWorkspaceConfig.PointQdrantAtDeadEndpoint(mintedRoot);
            }
        }

        return result;
    }

    // worktree init mints a store through the same path, so its config carries the same live endpoint.
    // Without the rewire a later command in a test would write real collections into the developer's Qdrant.
    private static bool MintsAStore(string[] args) =>
        args.Length > 0 && (args[0] == "init" || args[0] == "worktree");

    private static string? MintedRootFrom(string stdOut)
    {
        var start = stdOut.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdOut[start..]);
            return doc.RootElement.TryGetProperty("rootPath", out var root) ? root.GetString() : null;
        }
        catch (JsonException)
        {
            // Human-mode output is not JSON; the cwd probe above is still the right fallback.
            return null;
        }
    }

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunWithHomeAsync(
        string squeezrHome, string workingDir, params string[] args) =>
        StartAsync(workingDir, args, squeezrHome);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunPipedAsync(
        string squeezrHome, string stdin, string workingDir, params string[] args) =>
        StartAsync(workingDir, args, squeezrHome, stdin);

    // The same piped call against the home every other child in this run already shares — for fixtures that
    // registered their workspaces through RunAsync rather than into a scratch home of their own. A distinct
    // name, because (stdin, workingDir, args) and (home, stdin, workingDir, args) would bind alike.
    public static Task<(int ExitCode, string StdOut, string StdErr)> RunPipedInSharedHomeAsync(
        string stdin, string workingDir, params string[] args) =>
        StartAsync(workingDir, args, null, stdin);

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
