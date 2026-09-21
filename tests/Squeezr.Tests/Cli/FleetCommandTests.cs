using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class FleetCommandTests
{
    [Fact]
    public async Task Human_EmptyRegistry_PrintsFriendlyMessage()
    {
        var home = TestDirs.NewTempDir();
        var cwd = TestDirs.NewTempDir();

        var result = await RunCliWithHomeAsync(home, cwd, "fleet");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("No workspaces registered");
    }

    [Fact]
    public async Task Json_AliveDaemons_ReportPidAndUptime_BothKinds()
    {
        var name = UniqueName("fleet-alive");
        var root = await NewInitializedWorkspaceAsync(name);
        var startedAt = DateTime.UtcNow.AddMinutes(-90);
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, Environment.ProcessId, startedAt, DateTime.UtcNow);
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.ServeWatchKind, Environment.ProcessId, startedAt, DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entry = SingleEntry(result, name);
        var watch = entry.GetProperty("watch");
        watch.GetProperty("status").GetString().ShouldBe("alive");
        watch.GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
        watch.GetProperty("uptimeSeconds").GetInt64().ShouldBeGreaterThanOrEqualTo(90L * 60);
        var serveWatch = entry.GetProperty("serveWatch");
        serveWatch.GetProperty("status").GetString().ShouldBe("alive");
        serveWatch.GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task Human_AliveDaemon_RendersPidAndUptime()
    {
        var name = UniqueName("fleet-alive-human");
        var root = await NewInitializedWorkspaceAsync(name);
        WriteHeartbeat(
            SqueezrPaths.StoreDir(root),
            DaemonHeartbeat.DefaultKind,
            Environment.ProcessId,
            DateTime.UtcNow.AddMinutes(-90),
            DateTime.UtcNow);

        var output = await RunInProcessFleetAsync();

        output.ShouldContain($"pid {Environment.ProcessId}, up 1h 30m");
    }

    [Fact]
    public async Task Json_StaleHeartbeat_MarkedDead_WithRecordedPid()
    {
        var name = UniqueName("fleet-dead");
        var root = await NewInitializedWorkspaceAsync(name);
        var now = DateTime.UtcNow;
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, Environment.ProcessId, now.AddHours(-2), now.AddHours(-1));

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var watch = SingleEntry(result, name).GetProperty("watch");
        watch.GetProperty("status").GetString().ShouldBe("dead");
        watch.GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task Human_StaleHeartbeat_RendersDeadMarker()
    {
        var name = UniqueName("fleet-dead-human");
        var root = await NewInitializedWorkspaceAsync(name);
        var now = DateTime.UtcNow;
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, Environment.ProcessId, now.AddHours(-2), now.AddHours(-1));

        var output = await RunInProcessFleetAsync();

        output.ShouldContain("dead");
    }

    [Fact]
    public async Task Json_MissingHeartbeat_RendersNone()
    {
        var name = UniqueName("fleet-none");
        await NewInitializedWorkspaceAsync(name);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entry = SingleEntry(result, name);
        entry.GetProperty("watch").GetProperty("status").GetString().ShouldBe("none");
        entry.GetProperty("serveWatch").GetProperty("status").GetString().ShouldBe("none");
    }

    [Fact]
    public async Task StaleRootPath_IsMarkedStale_AndWarnsOnStderr()
    {
        var name = UniqueName("fleet-stale");
        var root = await NewInitializedWorkspaceAsync(name);
        Directory.Delete(root, recursive: true);

        var json = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");
        json.ExitCode.ShouldBe(0);
        SingleEntry(json, name).GetProperty("stale").GetBoolean().ShouldBeTrue();

        var human = await RunCliAsync(TestDirs.NewTempDir(), "fleet");
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldContain("stale");
        human.StdErr.ShouldContain("squeezr drop --store-key");
    }

    [Fact]
    public async Task Json_MissingMetaDb_PendingAndLastIndexedAreNull_CommandSucceeds()
    {
        var name = UniqueName("fleet-nometa");
        var root = await NewInitializedWorkspaceAsync(name);
        File.Delete(SqueezrPaths.MetaDbPath(root));

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entry = SingleEntry(result, name);
        entry.GetProperty("pendingChunks").ValueKind.ShouldBe(JsonValueKind.Null);
        entry.GetProperty("lastIndexedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Json_OutputIsCleanNdjson_NoWarningsOnStdoutOrStderr()
    {
        var name = UniqueName("fleet-clean");
        var root = await NewInitializedWorkspaceAsync(name);
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, Environment.ProcessId, DateTime.UtcNow, DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldBe(string.Empty);
        var entries = ParseNdjson(result.StdOut);
        entries.ShouldNotBeEmpty();
        var entry = entries.Single(e => e.GetProperty("name").GetString() == name);
        entry.GetProperty("storeKey").GetString().ShouldNotBeNullOrWhiteSpace();
        entry.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(root));
        entry.GetProperty("stale").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Json_WorkspaceWithAliveDaemon_SortsAboveIdleWorkspace()
    {
        var idleName = UniqueName("fleet-aaa-idle");
        await NewInitializedWorkspaceAsync(idleName);
        var aliveName = UniqueName("fleet-zzz-alive");
        var aliveRoot = await NewInitializedWorkspaceAsync(aliveName);
        WriteHeartbeat(
            SqueezrPaths.StoreDir(aliveRoot),
            DaemonHeartbeat.DefaultKind,
            Environment.ProcessId,
            DateTime.UtcNow,
            DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entries = ParseNdjson(result.StdOut);
        var aliveIndex = entries.FindIndex(e => e.GetProperty("name").GetString() == aliveName);
        var idleIndex = entries.FindIndex(e => e.GetProperty("name").GetString() == idleName);
        aliveIndex.ShouldBeGreaterThanOrEqualTo(0);
        idleIndex.ShouldBeGreaterThanOrEqualTo(0);
        aliveIndex.ShouldBeLessThan(idleIndex);
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<string> NewInitializedWorkspaceAsync(string name)
    {
        var root = TestDirs.NewTempDir();
        var init = await RunCliAsync(root, "init", "--name", name, "--json");
        init.ExitCode.ShouldBe(0);
        return root;
    }

    private static void WriteHeartbeat(string storeDir, string kind, int pid, DateTime startedAt, DateTime lastBeatAt)
    {
        var json = JsonSerializer.Serialize(new DaemonHeartbeat(pid, kind, startedAt, lastBeatAt), SqueezrJson.FileOptions);
        File.WriteAllText(DaemonHeartbeat.PathFor(storeDir, kind), json);
    }

    private static JsonElement SingleEntry((int ExitCode, string StdOut, string StdErr) result, string name) =>
        ParseNdjson(result.StdOut).Single(e => e.GetProperty("name").GetString() == name);

    private static List<JsonElement> ParseNdjson(string stdout) =>
        stdout.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    private static async Task<string> RunInProcessFleetAsync()
    {
        var previousConsole = AnsiConsole.Console;
        var console = new TestConsole();
        console.Profile.Width = 250;
        AnsiConsole.Console = console;
        try
        {
            var app = new CommandApp();
            app.Configure(config => config.AddCommand<FleetCommand>("fleet"));
            var code = await app.RunAsync(["fleet"]);
            code.ShouldBe(0);
            return console.Output;
        }
        finally
        {
            AnsiConsole.Console = previousConsole;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliWithHomeAsync(
        string squeezrHome, string workingDir, params string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");
        var startInfo = new ProcessStartInfo("dotnet", $"\"{cliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
