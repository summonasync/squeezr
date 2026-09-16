using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class CliWorkspaceDiscoveryTests
{
    private const string ClaudeProjectDirEnvironmentVariable = "CLAUDE_PROJECT_DIR";
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Search_FromNestedSubdirectory_FindsWorkspaceContent()
    {
        var root = TestDirs.NewTempDir();
        await InitAndIndexAsync(root);
        var nested = Directory.CreateDirectory(Path.Combine(root, "sub", "deep")).FullName;

        var search = await RunCliAsync(nested, "search", "Alpha", "--json", "--max-tokens", "1000");

        search.ExitCode.ShouldBe(0);
        var lines = search.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var doc = JsonDocument.Parse(lines[0]))
        {
            doc.RootElement.GetProperty("type").GetString().ShouldBe("match");
            doc.RootElement.GetProperty("path").GetString().ShouldBe("Alpha.cs");
        }
    }

    [Fact]
    public async Task Status_FromNestedSubdirectory_ReportsWorkspace()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "nestedws", "--json")).ExitCode.ShouldBe(0);
        var nested = Directory.CreateDirectory(Path.Combine(root, "a", "b")).FullName;

        var status = await RunCliAsync(nested, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.GetProperty("name").GetString().ShouldBe("nestedws");
    }

    [Fact]
    public async Task Symbol_FromNestedSubdirectory_ResolvesWorkspace()
    {
        var root = TestDirs.NewTempDir();
        await InitAndIndexAsync(root);
        var nested = Directory.CreateDirectory(Path.Combine(root, "x", "y")).FullName;

        var symbol = await RunCliAsync(nested, "symbol", "Alpha", "--json");

        symbol.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(symbol.StdOut.Trim());
        doc.RootElement.GetProperty("matches").EnumerateArray().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Status_OutsideWorkspace_ReturnsNotInitializedError()
    {
        var outside = TestDirs.NewTempDir();

        var plain = await RunCliAsync(outside, "status");
        plain.ExitCode.ShouldBe(1);
        plain.StdErr.ShouldContain("Workspace is not initialized");
        plain.StdErr.ShouldContain("squeezr init");

        var json = await RunCliAsync(outside, "status", "--json");
        json.ExitCode.ShouldBe(1);
        json.StdOut.ShouldBeEmpty();
        ParseStdErrError(json.StdErr).GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
    }

    [Fact]
    public async Task Search_OutsideWorkspace_ReturnsNotInitializedError()
    {
        var outside = TestDirs.NewTempDir();

        var search = await RunCliAsync(outside, "search", "Alpha", "--json");

        search.ExitCode.ShouldBe(1);
        search.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(search.StdErr);
        error.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
        error.GetProperty("message").GetString()!.ShouldContain("squeezr init");
    }

    [Fact]
    public async Task Search_InsideWorkspaceWithoutIndex_ReturnsIndexNotBuilt()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "noidx", "--json")).ExitCode.ShouldBe(0);

        var search = await RunCliAsync(root, "search", "Alpha", "--json");

        search.ExitCode.ShouldBe(1);
        search.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(search.StdErr);
        error.GetProperty("code").GetString().ShouldBe("index_not_built");
        error.GetProperty("message").GetString()!.ShouldContain("squeezr index");
    }

    [Fact]
    public async Task LegacyWorkspace_StatusFromSubdirectory_MigratesStoreAndPrintsNotice()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, SqueezrPaths.StoreDirName));
        File.WriteAllText(
            Path.Combine(root, SqueezrPaths.ManifestFileName),
            """{"name":"legacy","repos":[]}""");
        ConfigFile.Save(root, SqueezrConfig.CreateDefault());
        using (new MetaStore(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName), createIfMissing: true))
        {
        }

        var nested = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        var status = await RunCliAsync(nested, "status", "--json");

        status.ExitCode.ShouldBe(0);
        status.StdErr.ShouldContain("migrated to the central store");
        using (var doc = JsonDocument.Parse(status.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("name").GetString().ShouldBe("legacy");
        }

        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        var entry = WorkspaceRegistry.FindByRootPath(root);
        entry.ShouldNotBeNull();
        File.Exists(Path.Combine(SqueezrHome.WorkspacesDir, entry!.StoreKey, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_InsideExistingWorkspace_ReturnsAlreadyInsideError()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "outer", "--json")).ExitCode.ShouldBe(0);
        var nested = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;

        var init = await RunCliAsync(nested, "init", "--json");

        init.ExitCode.ShouldBe(1);
        init.StdErr.ShouldContain("already inside workspace");
        init.StdErr.ShouldContain(Path.GetFullPath(root));
        init.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task Serve_EnvFallback_ResolvesWorkspaceAndWarnsOnStderr()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "envws", "--json")).ExitCode.ShouldBe(0);
        var startDir = TestDirs.NewTempDir();

        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve")
        {
            WorkingDirectory = startDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment[ClaudeProjectDirEnvironmentVariable] = root;

        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"env-fallback-test","version":"1.0"}}}
            """);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        await stdoutTask;
        var stdErr = await stderrTask;

        process.ExitCode.ShouldBe(0);
        stdErr.ShouldContain($"workspace root resolved via CLAUDE_PROJECT_DIR: {Path.GetFullPath(root)}");
        stdErr.ShouldContain("squeezr serve: workspace");
    }

    private static async Task InitAndIndexAsync(string root)
    {
        (await RunCliAsync(root, "init", "--name", "discws", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Alpha.cs"), "class Alpha { }");
        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
    }

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
