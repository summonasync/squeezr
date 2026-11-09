using System.Diagnostics;
using Shouldly;
using Spectre.Console;
using Squeezr.Cli.Commands;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Tests.Infra.Autostart;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WatchdAutostartCliTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string UnitFileName = "squeezr-watchd.service";
    private const string WindowsBinary = @"C:\Program Files\Squeezr\squeezr.exe";
    private const string WindowsHome = @"D:\squeezr home";

    [Fact]
    public async Task WatchdHelp_ListsBothAutostartSubcommands()
    {
        var root = TestDirs.NewTempDir();

        var result = await CliTestProcess.RunAsync(root, "watchd", "--help");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("install");
        result.StdOut.ShouldContain("uninstall");
    }

    [Fact]
    public async Task BareWatchd_StillEntersTheSupervisorAndRefusesTheSecondInstance()
    {
        var storeHome = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        var lockPath = Path.Combine(storeHome, WatchdPaths.DirName, WatchdPaths.LockFileName);
        using var holder = new WatchdLock(lockPath);
        holder.TryAcquire().Acquired.ShouldBeTrue();

        var result = await CliTestProcess.RunWithHomeAsync(storeHome, root, "watchd");

        result.ExitCode.ShouldBe(1);
        result.StdErr.TrimEnd().ShouldBe(
            $"error: watchd is already running (pid {Environment.ProcessId} holds {lockPath}); " +
            "stop that instance first, or remove the lock once you have made sure it is gone.");
    }

    [Fact]
    public async Task Install_Child_WhenTheCarriedHomeIsNotLineSafe_RefusesWithoutApplyingAnything()
    {
        var configHome = TestDirs.NewTempDir();

        var result = await RunCliAsync(
            configHome,
            $"{Home}\nsecond-line",
            TestDirs.NewTempDir(),
            ["watchd", "install"]);

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("must not carry a line break");
        Directory.GetFiles(configHome, "*" + UnitFileName, SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Install_SecondRun_KeepsOneUnitOnDiskAndCarriesTheNewHome()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var unit = Path.Combine(unitDirectory, UnitFileName);

        var first = await RunInstallAsync(AutostartPlatform.SystemdUser, Binary, Home, applier);
        var second = await RunInstallAsync(AutostartPlatform.SystemdUser, Binary, "/var/tmp/other-home", applier);

        first.Exit.ShouldBe(0);
        second.Exit.ShouldBe(0);
        Directory.GetFiles(unitDirectory, "*.service").ShouldHaveSingleItem();
        File.ReadAllText(unit).ShouldContain("Environment=\"SQUEEZR_HOME=/var/tmp/other-home\"");
        first.StdOut.ToString().ShouldContain(unit);
        first.StdOut.ToString().ShouldContain($"SQUEEZR_HOME={Home}");
        second.StdOut.ToString().ShouldContain("SQUEEZR_HOME=/var/tmp/other-home");
    }

    [Fact]
    public async Task Install_OnTheWindowsBranch_HandsOverTheGeneratedLineAndAddsOnlyForce()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);
        var line = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, WindowsBinary, WindowsHome);

        var result = await RunInstallAsync(AutostartPlatform.WindowsScheduledTask, WindowsBinary, WindowsHome, applier);

        result.Exit.ShouldBe(0);
        result.StdOut.ToString().ShouldContain("Task Scheduler task 'squeezr-watchd'");
        var call = runner.Calls.ShouldHaveSingleItem();
        call.Verbatim.ShouldBeTrue();
        call.FileName.ShouldBe("schtasks.exe");
        call.Arguments.Single().ShouldBe(line + " /F");
    }

    [Fact]
    public async Task Install_WhenTheGeneratorRefusesTheInput_ThrowsInsteadOfCleaningIt()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);

        var error = await Should.ThrowAsync<ArgumentException>(() => WatchdInstallCommand.ApplyAsync(
            AutostartPlatform.WindowsScheduledTask,
            @"C:\Program Files\sq""ueezr\squeezr.exe",
            WindowsHome,
            applier,
            CancellationToken.None));

        error.ParamName.ShouldBe("binaryPath");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Uninstall_RemovesTheUnitAndNamesTheTarget()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var unit = Path.Combine(unitDirectory, UnitFileName);
        await applier.InstallAsync(
            WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, Home),
            CancellationToken.None);
        runner.Calls.Clear();

        var result = await RunUninstallAsync(applier);

        result.Exit.ShouldBe(0);
        File.Exists(unit).ShouldBeFalse();
        result.StdOut.ToString().ShouldContain("watchd autostart removed");
        result.StdOut.ToString().ShouldContain(unit);
        runner.Commands().ShouldBe(
        [
            $"systemctl --user disable --now {UnitFileName}",
            "systemctl --user daemon-reload",
        ]);
    }

    [Fact]
    public async Task Uninstall_WithNothingManaged_SucceedsAndSaysNothingWasRemoved()
    {
        var applier = new SystemdUserApplier(TestDirs.NewTempDir(), new FakeAutostartProcessRunner());

        var result = await RunUninstallAsync(applier);

        result.Exit.ShouldBe(0);
        result.StdErr.ToString().ShouldContain("nothing was removed");
        result.StdOut.ToString().ShouldBeEmpty();
    }

    private static Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunInstallAsync(
        AutostartPlatform platform,
        string binaryPath,
        string? squeezrHome,
        IWatchdAutostartApplier applier) =>
        RunUnitAsync(ct => WatchdInstallCommand.ApplyAsync(platform, binaryPath, squeezrHome, applier, ct));

    private static Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunUninstallAsync(
        IWatchdAutostartApplier applier) =>
        RunUnitAsync(ct => WatchdUninstallCommand.RemoveAsync(applier, ct));

    private static async Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunUnitAsync(
        Func<CancellationToken, Task<int>> run)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalConsole = AnsiConsole.Console;
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout) });
        // long managed paths must not be wrapped into pieces the assertions cannot match
        console.Profile.Width = 250;
        AnsiConsole.Console = console;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await run(CancellationToken.None);
            return (exit, stdout, stderr);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            AnsiConsole.Console = originalConsole;
        }
    }

    // The rejecting path earns an end-to-end run: it is the proof that the refusal reaches the
    // operator as an error rather than as a cleaned-up input, and that nothing was applied. Both
    // XDG_CONFIG_HOME and HOME go to temp dirs, so the child could not reach the real unit path
    // even if it got past the refusal.
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string configHome,
        string squeezrHome,
        string workingDir,
        string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");
        var startInfo = new ProcessStartInfo("dotnet", $"\"{cliDll}\" {string.Join(' ', args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["SQUEEZR_CLIENT_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["DOTNET_CLI_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["XDG_CONFIG_HOME"] = configHome;
        startInfo.Environment[SqueezrHome.HomeEnvironmentVariable] = squeezrHome;

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
