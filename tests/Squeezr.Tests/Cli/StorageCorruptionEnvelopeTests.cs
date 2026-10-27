using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// Polluted-fixture sweep (094): a corrupted store must surface as a coded envelope on stderr
// with rc=1, never as an unhandled rc=255 crash. Fail-open commands (search without config)
// stay fail-open on purpose.
[Collection("ProcessGlobals")]
public sealed class StorageCorruptionEnvelopeTests
{
    private const string Source = "public class Calc { public int Add(int a, int b) => a + b; }";

    [Fact]
    public async Task Index_OnUnreadableConfig_ReportsStorageUnavailableEnvelope()
    {
        var home = NewHome();
        var root = await NewIndexedWorkspaceAsync(home);
        LockConfig(home);

        var run = await CliTestProcess.RunPipedAsync(home, "", root, "index", "--json");

        run.ExitCode.ShouldBe(1, Describe(run));
        ErrorEnvelope(run).GetProperty("code").GetString().ShouldBe("storage_unavailable");
    }

    [Fact]
    public async Task Status_OnUnreadableConfig_ReportsEnvelopeInsteadOfCrash()
    {
        var home = NewHome();
        var root = await NewIndexedWorkspaceAsync(home);
        LockConfig(home);

        var run = await CliTestProcess.RunPipedAsync(home, "", root, "status", "--json");

        run.ExitCode.ShouldBe(1, Describe(run));
        ErrorEnvelope(run).GetProperty("code").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task RankEval_OnUnreadableConfig_StaysFailOpenWithWarning()
    {
        var home = NewHome();
        var root = await NewIndexedWorkspaceAsync(home);
        var setPath = Path.Combine(root, "golden.json");
        await File.WriteAllTextAsync(setPath, """[{"query":"calc","expected":["Calc.cs"],"note":"t"}]""");
        LockConfig(home);

        var run = await CliTestProcess.RunPipedAsync(home, "", root, "rank-eval", "--set", setPath, "--json");

        run.ExitCode.ShouldBe(0, Describe(run));
        run.StdErr.ShouldContain("could not be read");
    }

    [Fact]
    public async Task Search_OnMissingConfig_StaysFailOpen()
    {
        var home = NewHome();
        var root = await NewIndexedWorkspaceAsync(home);
        File.Delete(ConfigPath(home));

        var run = await CliTestProcess.RunPipedAsync(home, "", root, "search", "calc", "--json");

        run.ExitCode.ShouldBe(0, Describe(run));
        run.StdErr.ShouldContain("config file not found");
    }

    [Fact]
    public async Task Init_OnReadOnlyHome_ReportsStorageUnavailableEnvelope()
    {
        var home = TestDirs.NewTempDir();
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
        var root = TestDirs.NewTempDir();
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            var run = await CliTestProcess.RunPipedAsync(home, "", root, "init", "--name", "locked", "--json");

            run.ExitCode.ShouldBe(1, Describe(run));
            ErrorEnvelope(run).GetProperty("code").GetString().ShouldBe("storage_unavailable");
        }
        finally
        {
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
    }

    private static string NewHome()
    {
        var home = TestDirs.NewTempDir();
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
        return home;
    }

    private static async Task<string> NewIndexedWorkspaceAsync(string home)
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunPipedAsync(home, "", root, "init", "--json")).ExitCode.ShouldBe(0);
        await File.WriteAllTextAsync(Path.Combine(root, "Calc.cs"), Source);
        (await CliTestProcess.RunPipedAsync(home, "", root, "add", ".")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunPipedAsync(home, "", root, "index")).ExitCode.ShouldBe(0);
        return root;
    }

    private static string ConfigPath(string home) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, Path.GetFileName(Directory.EnumerateDirectories(
                Path.Combine(home, SqueezrPaths.WorkspacesDirName)).Single()),
            SqueezrPaths.ConfigFileName);

    private static void LockConfig(string home) =>
        File.SetUnixFileMode(ConfigPath(home), UnixFileMode.None);

    private static JsonElement ErrorEnvelope((int ExitCode, string StdOut, string StdErr) run)
    {
        var lines = run.StdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return JsonDocument.Parse(lines[^1]).RootElement.GetProperty("error");
    }

    private static string Describe((int ExitCode, string StdOut, string StdErr) run) =>
        $"stderr:{Environment.NewLine}{run.StdErr}{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}";
}
