using System.Diagnostics;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
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

    private static async Task<string> AwaitLineAsync(Process process, Func<string, bool> match)
    {
        using var timeout = new CancellationTokenSource(LineTimeout);
        while (true)
        {
            var lineTask = process.StandardOutput.ReadLineAsync();
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

    private static Process StartWatchProcess(string workingDir)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" watch")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        return Process.Start(startInfo)!;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string workingDir, params string[] args)
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

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string NewWatchWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
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
