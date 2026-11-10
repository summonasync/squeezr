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

        var result = await RunInstalledCliAsync(
            configHome,
            $"{Home}\nsecond-line",
            TestDirs.NewTempDir(),
            ["watchd", "install"]);

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("must not carry a line break");
        Directory.GetFiles(configHome, "*" + UnitFileName, SearchOption.AllDirectories).ShouldBeEmpty();
    }

    // The shape hard-review 110-03 measured: a muxer-launched CLI resolves its own program to the
    // entry assembly, and a unit naming it dies at exec with 126 while looking installed. The control
    // is the test above — the same refusal harness launched from the apphost gets past this guard and
    // reaches the generator, so a guard that refused everything would not leave that one green.
    [LinuxFact]
    public async Task Install_Child_WhenTheResolvedProgramIsAMuxerAssembly_RefusesAndWritesNoUnit()
    {
        var configHome = TestDirs.NewTempDir();
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

        var result = await RunMuxedCliAsync(
            configHome,
            TestDirs.NewTempDir(),
            TestDirs.NewTempDir(),
            ["watchd", "install"]);

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain(cliDll);
        result.StdErr.ShouldContain("managed assembly");
        result.StdErr.ShouldContain("installed squeezr binary");
        result.StdOut.ShouldNotContain("autostart installed");
        Directory.Exists(Path.Combine(configHome, "systemd")).ShouldBeFalse();
        Directory.GetFiles(configHome, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    // The escaping IOException class is the operator's error channel: unfiltered it leaves the CLI as
    // "Error: …" on stdout with exit 255, on the very path that writes files. Here the unit path is a
    // directory, so the write refuses before any tool is asked to speak.
    [LinuxFact]
    public async Task Install_Child_WhenTheUnitPathCannotBeWritten_RefusesOnStderrWithExitOne()
    {
        var configHome = TestDirs.NewTempDir();
        var unitDirectory = Path.Combine(configHome, "systemd", "user");
        Directory.CreateDirectory(Path.Combine(unitDirectory, UnitFileName));

        var result = await RunInstalledCliAsync(
            configHome,
            TestDirs.NewTempDir(),
            TestDirs.NewTempDir(),
            ["watchd", "install"]);

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("error:");
        result.StdErr.ShouldContain(UnitFileName);
        result.StdOut.ShouldNotContain("Error:");
        result.StdOut.ShouldNotContain("autostart installed");
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

    // A systemctl that will not answer about disable must not be able to keep our unit on disk: the
    // file is the step that ends the autostart, so the removal stands and what the tool said travels
    // to stderr as a warning instead of turning uninstall into a failure it can never finish.
    [Fact]
    public async Task Uninstall_WhenDisableRefusesToAnswer_RemovesTheUnitSucceedsAndWarns()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("disable")
            ? new AutostartProcessResult(1, string.Empty, "Failed to connect to bus: No such file or directory")
            : FakeAutostartProcessRunner.Ok);
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var unit = Path.Combine(unitDirectory, UnitFileName);
        await applier.InstallAsync(
            WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, Home),
            CancellationToken.None);

        var result = await RunUninstallAsync(applier);

        result.Exit.ShouldBe(0);
        File.Exists(unit).ShouldBeFalse();
        result.StdOut.ToString().ShouldContain("watchd autostart removed");
        result.StdErr.ToString().ShouldContain(
            "warning: 'systemctl' failed to stop and unhook the watch daemon (exit 1)");
        result.StdErr.ToString().ShouldContain("Failed to connect to bus");
    }

    // The uninstall half of the same filter: the deletion of the managed file is raw filesystem work,
    // so a directory that will not let its entry go has to reach the operator as an error line, not as
    // the CLI's unhandled "Error:" on stdout with exit 255. systemctl is intercepted by a shim on the
    // child's PATH — the refusal being measured belongs to the delete, not to the tool.
    [LinuxFact]
    public async Task Uninstall_Child_WhenTheUnitCannotBeDeleted_FailsOnStderrWithExitOne()
    {
        var configHome = TestDirs.NewTempDir();
        var unitDirectory = Path.Combine(configHome, "systemd", "user");
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(
            Path.Combine(unitDirectory, UnitFileName),
            WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, Home));
        var shimDirectory = WriteSystemctlShim();
        SetOwnerMode(unitDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await RunCliAsync(
                AppHostPath(),
                null,
                configHome,
                TestDirs.NewTempDir(),
                TestDirs.NewTempDir(),
                ["watchd", "uninstall"],
                shimDirectory);

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("error:");
            result.StdErr.ShouldContain(UnitFileName);
            result.StdOut.ShouldNotContain("Error:");
            result.StdOut.ShouldNotContain("watchd autostart removed");
        }
        finally
        {
            SetOwnerMode(
                unitDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    // The rejecting paths earn an end-to-end run: they are the proof that the refusal reaches the
    // operator as an error rather than as a cleaned-up input or an unhandled "Error:" line, and that
    // nothing was applied. Both XDG_CONFIG_HOME and HOME go to temp dirs, so the child could not
    // reach the real unit path even if it got past the refusal.
    //
    // Two launch shapes matter and are both driven: the apphost is what an installed squeezr is and
    // what the executability guard accepts, 'dotnet <assembly>' is the shape the guard refuses — a
    // unit naming a managed assembly dies at exec with 126 while looking installed.
    private static Task<(int ExitCode, string StdOut, string StdErr)> RunInstalledCliAsync(
        string configHome,
        string squeezrHome,
        string workingDir,
        string[] args) =>
        RunCliAsync(AppHostPath(), null, configHome, squeezrHome, workingDir, args);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunMuxedCliAsync(
        string configHome,
        string squeezrHome,
        string workingDir,
        string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

        return RunCliAsync("dotnet", $"\"{cliDll}\"", configHome, squeezrHome, workingDir, args);
    }

    private static string AppHostPath() =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "squeezr.exe" : "squeezr");

    // The child must never reach a real bus: the shim answers every systemctl verb with success and is
    // placed first on the child's PATH, so the only thing a test can measure through it is what the
    // CLI did before or after asking the tool.
    private static string WriteSystemctlShim()
    {
        var shimDirectory = TestDirs.NewTempDir();
        var shim = Path.Combine(shimDirectory, "systemctl");
        File.WriteAllText(shim, "#!/bin/sh\nexit 0\n");
        SetOwnerMode(
            shim,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return shimDirectory;
    }

    // The mode rules are Linux file semantics — the tests that lean on them are [LinuxFact], and the
    // host check here keeps the analyzer from calling these sites platform-reachable.
    private static void SetOwnerMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string program,
        string? leadingArgument,
        string configHome,
        string squeezrHome,
        string workingDir,
        string[] args,
        string? pathShimDirectory = null)
    {
        var argumentLine = leadingArgument is null
            ? string.Join(' ', args)
            : $"{leadingArgument} {string.Join(' ', args)}";
        var startInfo = new ProcessStartInfo(program, argumentLine)
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
        if (pathShimDirectory is not null)
        {
            var inherited = Environment.GetEnvironmentVariable("PATH");
            startInfo.Environment["PATH"] = string.IsNullOrEmpty(inherited)
                ? pathShimDirectory
                : $"{pathShimDirectory}{Path.PathSeparator}{inherited}";
        }

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
