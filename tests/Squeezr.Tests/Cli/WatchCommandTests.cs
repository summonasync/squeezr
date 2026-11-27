using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WatchCommandTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Help_ListsWatchCommand()
    {
        var root = TestDirs.NewTempDir();

        var result = await RunCliAsync(root, "watch", "--help");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("watch");
        result.StdOut.ShouldContain("Watch the workspace");
    }

    [Fact]
    public async Task Watch_OutsideWorkspace_ExitsWithInitHint()
    {
        var root = TestDirs.NewTempDir();

        var result = await RunCliAsync(root, "watch");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("not initialized");
        result.StdErr.ShouldContain("squeezr init");
    }

    [Fact]
    public async Task Watch_PipedOutput_ReportsPassOnExternalEdit()
    {
        var workspace = NewWatchWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"watchold\"; }");
        new WorkspaceIndexer().Index(workspace);

        using var process = StartWatchProcess(workspace);
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await AwaitLineAsync(process, line => line.Contains("watching"));

            WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"watchfresh\"; }");

            var started = await AwaitLineAsync(process, line => line.Contains("pass started"));
            started.ShouldContain("changed paths");
            var completed = await AwaitLineAsync(process, line => line.Contains("pass completed"));
            completed.ShouldContain("~1");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Watch_TokenCancelledMidRun_ExitsGracefully()
    {
        var workspace = NewWatchWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        new WorkspaceIndexer().Index(workspace);

        var previousConsole = AnsiConsole.Console;
        var previousDir = Environment.CurrentDirectory;
        AnsiConsole.Console = new TestConsole();
        Environment.CurrentDirectory = workspace;
        try
        {
            var app = new CommandApp();
            app.Configure(config => config.AddCommand<WatchCommand>("watch"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var code = await app.RunAsync(["watch"], cts.Token);

            code.ShouldBe(0);
        }
        finally
        {
            Environment.CurrentDirectory = previousDir;
            AnsiConsole.Console = previousConsole;
        }
    }

    [Fact]
    public async Task Watch_InteractiveConsole_RendersLiveStatus_ExitsGracefully()
    {
        var workspace = NewWatchWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        new WorkspaceIndexer().Index(workspace);

        var previousConsole = AnsiConsole.Console;
        var previousDir = Environment.CurrentDirectory;
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        AnsiConsole.Console = console;
        Environment.CurrentDirectory = workspace;
        try
        {
            var app = new CommandApp();
            app.Configure(config => config.AddCommand<WatchCommand>("watch"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            var code = await app.RunAsync(["watch"], cts.Token);

            code.ShouldBe(0);
            console.Output.ShouldContain("workspace");
            console.Output.ShouldContain("waiting for changes");
        }
        finally
        {
            Environment.CurrentDirectory = previousDir;
            AnsiConsole.Console = previousConsole;
        }
    }

    [Fact]
    public async Task Watch_OwnerClaimHeldElsewhere_StartsNoWatcherAndNamesTheOwnerPid()
    {
        var workspace = NewWatchWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"watchclaim1\"; }");
        new WorkspaceIndexer().Index(workspace);
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var owner = new WatchOwnerClaim(storeDir);
        owner.TryAcquire().IsOwner.ShouldBeTrue();

        var result = await RunCliAsync(workspace, "watch");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldContain($"watch not started: pid {Environment.ProcessId} owns this store");
        result.StdErr.ShouldContain("no watch loop runs in this process");
        result.StdErr.ShouldNotContain("watching");
        result.StdOut.ShouldNotContain("watching");
        File.Exists(DaemonHeartbeat.PathFor(storeDir, DaemonHeartbeat.DefaultKind)).ShouldBeFalse(
            "the refused watch armed a heartbeat writer on a store it does not own");
        owner.ReadOwner()!.Pid
            .ShouldBe(Environment.ProcessId, "the refused watch reaped or overwrote the live owner's claim");
    }

    [LinuxFact]
    public async Task Watch_OwnerDeadWithFreshBeat_RefusesWithoutAwaitingTheGrace()
    {
        var workspace = NewWatchWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var deadPid = SpawnReapedPid();
        WriteOwnerMarker(storeDir, deadPid, beatAge: TimeSpan.FromSeconds(5));

        using var process = StartWatchProcess(workspace);
        try
        {
            var decline = await AwaitLineAsync(process.StandardError, line => line.Contains("watch not started"));

            decline.ShouldContain($"pid {deadPid} owns this store");
            WatchOwnerMarker.TryRead(storeDir)!.Pid.ShouldBe(deadPid,
                "the refused watch reaped the dead owner's claim instead of leaving the grace window alone");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
        }
    }

    [LinuxFact]
    public async Task Watch_AwaitClaim_WaitsOutDeadOwnerGraceThenTakesOver()
    {
        var workspace = NewWatchWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"watchgrace\"; }");
        new WorkspaceIndexer().Index(workspace);
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var deadPid = SpawnReapedPid();
        WriteOwnerMarker(storeDir, deadPid, beatAge: TimeSpan.FromSeconds(24));

        using var process = StartWatchProcess(workspace, "--await-claim");
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await AwaitLineAsync(process, line => line.Contains("watching"));

            new WatchOwnerClaim(storeDir).ReadOwner()!.Pid.ShouldBe(process.Id);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
        }

        (await stderrTask).ShouldNotContain("watch not started");
    }

    [Fact]
    public async Task Watch_FirstClaimant_BecomesOwner_StartsWatcher_WritesHeartbeat()
    {
        var workspace = NewWatchWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"watchclaim2\"; }");
        new WorkspaceIndexer().Index(workspace);
        var storeDir = SqueezrPaths.StoreDir(workspace);

        using var process = StartWatchProcess(workspace);
        var stderrTask = process.StandardError.ReadToEndAsync();
        string claimed;
        try
        {
            claimed = await AwaitLineAsync(process, line => line.Contains("watching"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
        }

        claimed.ShouldNotBeNullOrWhiteSpace();
        new WatchOwnerClaim(storeDir).ReadOwner()!.Pid
            .ShouldBe(process.Id, "the first claimant recorded a claim naming somebody else");
        var heartbeat = DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.DefaultKind);
        heartbeat.ShouldNotBeNull("the owning watch armed no heartbeat");
        heartbeat.Pid.ShouldBe(process.Id);
        heartbeat.Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        (await stderrTask).ShouldNotContain("watch not started");
    }

    private static Task<string> AwaitLineAsync(Process process, Func<string, bool> match) =>
        AwaitLineAsync(process.StandardOutput, match);

    private static async Task<string> AwaitLineAsync(TextReader reader, Func<string, bool> match)
    {
        using var timeout = new CancellationTokenSource(LineTimeout);
        while (true)
        {
            var lineTask = reader.ReadLineAsync();
            var winner = await Task.WhenAny(lineTask, Task.Delay(Timeout.Infinite, timeout.Token));
            if (winner != lineTask)
            {
                throw new TimeoutException($"watch did not print the expected line within {LineTimeout.TotalSeconds}s");
            }

            var line = await lineTask;
            if (line is null)
            {
                throw new InvalidOperationException("watch exited before printing the expected line");
            }
            if (match(line))
            {
                return line;
            }
        }
    }

    private static Process StartWatchProcess(string workingDir, params string[] arguments)
    {
        var args = string.Join(" ", new[] { "watch" }.Concat(arguments));
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {args}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        return Process.Start(startInfo)!;
    }

    private static int SpawnReapedPid()
    {
        using var process = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false });
        var pid = process!.Id;
        process.Kill();
        process.WaitForExit(5000).ShouldBeTrue();
        return pid;
    }

    private static void WriteOwnerMarker(string storeDir, int pid, TimeSpan beatAge)
    {
        var stamped = DateTime.UtcNow - beatAge;
        var marker = new WatchOwnerMarker(pid, Environment.MachineName, DaemonHeartbeat.DefaultKind, stamped, stamped);
        File.WriteAllText(
            WatchOwnerMarker.PathFor(storeDir),
            JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    private static string NewWatchWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
