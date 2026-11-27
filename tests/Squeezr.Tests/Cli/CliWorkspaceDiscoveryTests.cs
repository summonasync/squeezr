using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
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
    public async Task HookPreRead_InsideRegisteredChild_RecordsTelemetryToTheChildStore()
    {
        var home = TestDirs.NewTempDir();
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var child = Path.Combine(main, ".lanes", "lane-x");
        TestGitRepo.AddWorktree(main, child, "feat/116-test-lane");
        var file = Path.Combine(child, "src", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "class A { }");

        (await CliTestProcess.RunWithHomeAsync(home, main, "init", "--name", "parentws", "--json"))
            .ExitCode.ShouldBe(0);
        var registered = await CliTestProcess.RunWithHomeAsync(home, child, "worktree", "init", "--json");
        registered.ExitCode.ShouldBe(0);
        using (var state = JsonDocument.Parse(registered.StdOut.Trim()))
        {
            state.RootElement.GetProperty("state").GetString().ShouldBe(
                "Registered", "the child store has to come from the verb, not from a hand-written row");
        }

        var parentKey = StoreKeyOf(home, main);
        var childKey = StoreKeyOf(home, child);

        // The parent row is the first one on disk, so arrival order hands the read to the parent;
        // only depth hands it to the child, and the child's telemetry log is where that shows.
        var result = await CliTestProcess.RunPipedAsync(
            home,
            "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"" + file + "\"}}",
            child, "hook", "pre-read");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldBeEmpty();
        var childLog = Path.Combine(StoreDir(home, childKey), SqueezrPaths.QueriesLogFileName);
        File.Exists(childLog).ShouldBeTrue();
        File.Exists(Path.Combine(StoreDir(home, parentKey), SqueezrPaths.QueriesLogFileName))
            .ShouldBeFalse();
        using var doc = JsonDocument.Parse(File.ReadAllText(childLog));
        doc.RootElement.GetProperty("tool").GetString().ShouldBe("pre-read");
        doc.RootElement.GetProperty("params").GetProperty("file").GetString().ShouldBe(file);
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
        SemanticConfigFile.Save(root, SemanticConfig.Bind(SqueezrConfig.CreateDefault()));
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
        var message = ParseStdErrError(init.StdErr).GetProperty("message").GetString();
        message.ShouldContain("already inside workspace");
        message.ShouldContain(Path.GetFullPath(root));
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

    // The second FindContainingPath call site of the hook: the advice leg consults the guard ledger of whichever
    // row the resolution hands it, so a child whose own config guards nothing must answer where the parent's
    // config says to stay silent. The two paths are the same relative path in two trees, and only the nearest
    // child rule tells them apart.
    [Fact]
    public async Task HookPreRead_AdviceLegInRegisteredChild_GuardsWithTheChildLedger()
    {
        var home = TestDirs.NewTempDir();
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var child = Path.Combine(main, ".lanes", "lane-guard");
        TestGitRepo.AddWorktree(main, child, "feat/116-hook-guard");

        (await CliTestProcess.RunWithHomeAsync(home, main, "init", "--name", "guardparent", "--json"))
            .ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(home, child, "worktree", "init", "--reindex", "--no-semantic", "--json"))
            .ExitCode.ShouldBe(0);

        // guarded only in the parent's ledger: a resolution that slipped to the parent for the child's path
        // would answer that read with the parent's silence. Written into the parent's store dir by path,
        // because this process does not carry the child's pinned SQUEEZR_HOME.
        GuardInStore(home, main, "infra/VaultKeys.cs");
        var big = new string('x', 300 * 1024);
        foreach (var root in new[] { main, child })
        {
            Directory.CreateDirectory(Path.Combine(root, "infra"));
            File.WriteAllText(Path.Combine(root, "infra", "VaultKeys.cs"), big);
        }

        var childRun = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(Path.Combine(child, "infra", "VaultKeys.cs")), child, "hook", "pre-read");
        var parentRun = await CliTestProcess.RunPipedAsync(
            home, ReadPayload(Path.Combine(main, "infra", "VaultKeys.cs")), main, "hook", "pre-read");

        childRun.ExitCode.ShouldBe(0);
        // the child ledger does not guard this path, so the size question is answered
        childRun.StdOut.ShouldContain("expand_span");
        parentRun.StdOut.ShouldBeEmpty();

        // the store the hook resolved is the child's own: the row lands under the child's registry key
        var childKey = StoreKeyOf(home, child);
        File.Exists(Path.Combine(StoreDir(home, childKey), SqueezrPaths.QueriesLogFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Install_ProjectFromUnregisteredNestedCheckout_WritesTheBlockThereAndDoesNotClaimTheParent()
    {
        var home = TestDirs.NewTempDir();
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        (await CliTestProcess.RunWithHomeAsync(home, main, "init", "--name", "nestedparent", "--json"))
            .ExitCode.ShouldBe(0);
        var lane = Path.Combine(main, ".lanes", "lane-block");
        TestGitRepo.AddWorktree(main, lane, "feat/116-nested-block");

        var run = await CliTestProcess.RunWithHomeAsync(home, lane, "mcp", "install", "--project");

        run.ExitCode.ShouldBe(0);
        var written = File.ReadAllText(Path.Combine(lane, "AGENTS.md"));
        written.ShouldContain(ProjectInstructions.StartMarker);
        // the block for a checkout with no store must not claim one, and must not be written at the parent
        written.ShouldNotContain("This project is indexed by squeezr");
        written.ShouldContain(SessionLocation.Display(main));
        written.ShouldContain("squeezr worktree init");
        written.ShouldContain("re-run `squeezr mcp install --project`");
        File.Exists(Path.Combine(main, "AGENTS.md")).ShouldBeFalse();

        // once the lane owns a store the same door tells the ordinary story at the same path
        (await CliTestProcess.RunWithHomeAsync(home, lane, "worktree", "init", "--reindex", "--no-semantic", "--json"))
            .ExitCode.ShouldBe(0);
        await CliTestProcess.RunWithHomeAsync(home, lane, "mcp", "install", "--project");

        // a checkout that owns its store is indexed, and the block says so
        File.ReadAllText(Path.Combine(lane, "AGENTS.md"))
            .ShouldContain("This project is indexed by squeezr");
    }

    private static string ReadPayload(string filePath) =>
        "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"" + filePath + "\"}}";

    // The guard ledger lives in the store the resolution names, and this process does not carry the home a CLI
    // child pinned, so the config is written by path rather than through the in-process config helper.
    private static void GuardInStore(string home, string rootPath, params string[] globs)
    {
        var config = SemanticConfig.Bind(SqueezrConfig.CreateDefault());
        File.WriteAllText(
            Path.Combine(StoreDir(home, StoreKeyOf(home, rootPath)), SqueezrPaths.ConfigFileName),
            JsonSerializer.Serialize(
                config with { Indexing = config.Indexing with { SecretGuardExtra = globs } },
                SqueezrJson.FileOptions));
    }

    private static string StoreDir(string home, string storeKey) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);

    private static string StoreKeyOf(string home, string rootPath)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(rootPath);
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)));
        return doc.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("rootPath").GetString() == canonical)
            .GetProperty("storeKey")
            .GetString()!;
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
