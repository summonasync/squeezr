using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Squeezr.Semantic.Configuration;
using Shouldly;
using Spectre.Console;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Core.Configuration;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Infra.Autostart;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WatchdAutostartCliTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string ManagedWorkspaceName = "retiree2e";
    private const string UnitFileName = "squeezr-watchd.service";
    private const string WindowsHome = @"D:\squeezr home";
    private const string WindowsHomeWithQuote = @"D:\evil"" & calc & ""x";
    private const string LaunchDomain = "gui/502";
    private const string RemedyFragment = "installed squeezr binary";
    private const string StaleRemedyFragment = "no muxer in front";
    private const string RetirementFragment = "watchd is retiring after an upgrade";
    private static readonly TimeSpan RetireLineBudget = TimeSpan.FromSeconds(15);

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

    // The live proof of the pair, measured on a real child in both launch shapes. 'dotnet squeezr.dll
    // watchd install' is how this build is run out of a repo, and the unit it writes has to be the one
    // systemd can exec: the muxer as the program, the assembly as the argument behind it (the shape
    // hard-review 110-03 measured as broken before T9 — a unit naming the assembly alone dies at exec
    // with 126 while looking installed). The apphost arm drives the same install in the same harness,
    // so a resolver that always picked one of the two shapes could not leave both arms green. systemctl
    // is a PATH shim recording every argv it saw, so the ledger says exactly what install asked the bus.
    [LinuxFact]
    public async Task Install_Child_WhenRunThroughTheMuxer_WritesAUnitThePlatformCanExec()
    {
        var configHome = TestDirs.NewTempDir();
        var ledgerPath = Path.Combine(TestDirs.NewTempDir(), "systemctl.ledger");
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");
        var shimDirectory = WriteSystemctlShim(ledgerPath);

        var result = await RunMuxedCliAsync(
            configHome,
            TestDirs.NewTempDir(),
            TestDirs.NewTempDir(),
            ["watchd", "install"],
            shimDirectory);

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldContain("watchd autostart installed");
        var unit = ManagedUnitText(configHome);
        var command = WatchdUnitProgram.Read(AutostartPlatform.SystemdUser, unit);
        command.ShouldNotBeNull();
        Path.GetFileName(command.Executable).ShouldBe("dotnet");
        command.Executable.ShouldNotBe(cliDll);
        Launchability.Refusal(AutostartPlatform.SystemdUser, command.Executable).ShouldBeNull();
        command.Arguments.ShouldBe(
            [cliDll, WatchdUnitGenerator.WatchdVerb, WatchdUnitGenerator.AutostartFlag]);
        unit.ShouldContain($"""ExecStart="{command.Executable}" "{cliDll}" watchd --autostart""");
        ReadLedger(ledgerPath).ShouldBe(
        [
            "--user daemon-reload",
            "--user enable --now squeezr-watchd.service",
        ]);
    }

    [LinuxFact]
    public async Task Install_Child_WhenRunFromTheAppHost_WritesAUnitNamingThatProgramAlone()
    {
        var configHome = TestDirs.NewTempDir();
        var ledgerPath = Path.Combine(TestDirs.NewTempDir(), "systemctl.ledger");
        var shimDirectory = WriteSystemctlShim(ledgerPath);

        var result = await RunInstalledCliAsync(
            configHome,
            TestDirs.NewTempDir(),
            TestDirs.NewTempDir(),
            ["watchd", "install"],
            shimDirectory);

        result.ExitCode.ShouldBe(0, result.StdErr);
        var unit = ManagedUnitText(configHome);
        var command = WatchdUnitProgram.Read(AutostartPlatform.SystemdUser, unit);
        command.ShouldNotBeNull();
        Path.GetFileName(command.Executable).ShouldBe("squeezr");
        command.Arguments.ShouldBe([WatchdUnitGenerator.WatchdVerb, WatchdUnitGenerator.AutostartFlag]);
        unit.ShouldContain($"""ExecStart="{command.Executable}" watchd --autostart""");
        ReadLedger(ledgerPath).ShouldBe(
        [
            "--user daemon-reload",
            "--user enable --now squeezr-watchd.service",
        ]);
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

    // The seam checks launchability before it generates anything, so the paths a test hands over have
    // to be programs the guard can accept: an executable file for the unix branches, an existing
    // '.exe' for the Windows branch, where the host file mode is not the question.
    [Fact]
    public async Task Install_SecondRun_KeepsOneUnitOnDiskAndCarriesTheNewHome()
    {
        var program = TestProgram.Executable("squeezr");
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var unit = Path.Combine(unitDirectory, UnitFileName);

        var first = await RunInstallAsync(AutostartPlatform.SystemdUser, program, Home, applier);
        var second = await RunInstallAsync(AutostartPlatform.SystemdUser, program, "/var/tmp/other-home", applier);

        first.Exit.ShouldBe(0);
        second.Exit.ShouldBe(0);
        Directory.GetFiles(unitDirectory, "*.service").ShouldHaveSingleItem();
        File.ReadAllText(unit).ShouldContain("Environment=\"SQUEEZR_HOME=/var/tmp/other-home\"");
        first.StdOut.ToString().ShouldContain(unit);
        first.StdOut.ToString().ShouldContain($"SQUEEZR_HOME={Home}");
        second.StdOut.ToString().ShouldContain("SQUEEZR_HOME=/var/tmp/other-home");
    }

    // The manual-instance collision, warned about instead of decided for the operator (owner decision
    // 2026-11-09). The holder here is a real WatchdLock taken by this process, which is exactly the
    // shape of a watchd somebody started by hand: the pid and start time both answer live, so the
    // notice has to name it — and the install itself has to stay byte-for-byte what T2 closed.
    [Fact]
    public async Task Install_WhenALiveWatchdHoldsTheLock_WarnsOnStderrAndStillInstalls()
    {
        var home = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var lockPath = WatchdPaths.LockPathUnder(home);
        using var holder = new WatchdLock(lockPath);
        holder.TryAcquire().Acquired.ShouldBeTrue();

        var result = await RunInstallAsync(AutostartPlatform.SystemdUser, program, home, applier);

        result.Exit.ShouldBe(0);
        var notice = result.StdErr.ToString();
        notice.ShouldContain($"warning: a live watchd (pid {Environment.ProcessId}) already holds {lockPath}");
        notice.ShouldContain("refuse the lock");
        notice.ShouldContain("start limit");
        notice.ShouldContain("left failed");
        notice.ShouldContain("Stop that instance and run install again");
        notice.ShouldContain("systemctl --user restart squeezr-watchd");
        // Both exits sit behind the start limit the unit declares: a plain restart inside the window can
        // be refused, so the line has to name the window and the flush that gets around it (110-11
        // WARN-1). These are substring asserts — the notice is one wrapped sentence, not a fixed line.
        notice.ShouldContain("systemctl --user reset-failed squeezr-watchd");
        notice.ShouldContain("60-second window");
        result.StdOut.ToString().ShouldContain("watchd autostart installed");
        // The notice promises a start limit, and the unit install just wrote has to be the one carrying
        // it; "does not undo the install" is measured on disk here, not only through the exit code.
        var unit = Directory.GetFiles(unitDirectory, "*.service").ShouldHaveSingleItem();
        File.ReadAllText(unit).ShouldContain("StartLimitIntervalSec=60");
        File.ReadAllText(unit).ShouldContain("StartLimitBurst=5");
        runner.Commands().ShouldBe(
        [
            "systemctl --user daemon-reload",
            "systemctl --user enable --now squeezr-watchd.service",
        ]);
    }

    // The other half of the same line: nothing is claimed when the singleton is free, because the
    // instance the entry starts is going to take the lock and run. The read is the only thing install
    // does with the file, so an empty lock directory must leave stderr exactly as empty as it was.
    [Fact]
    public async Task Install_WithTheLockFree_LeavesStderrEmpty()
    {
        var home = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        var applier = new SystemdUserApplier(TestDirs.NewTempDir(), new FakeAutostartProcessRunner());

        var result = await RunInstallAsync(AutostartPlatform.SystemdUser, program, home, applier);

        result.Exit.ShouldBe(0);
        result.StdErr.ToString().ShouldBeEmpty();
        result.StdOut.ToString().ShouldContain("watchd autostart installed");
    }

    // A lock file no reader can parse is stale by the rule UnparseableLockFile_DoesNotBlock pins: the
    // instance the entry starts steals it and comes up, so warning about a collision that will not
    // happen would be the lie, and silence is the honest answer.
    [Fact]
    public async Task Install_WhenTheLockFileCannotBeParsed_LeavesStderrEmpty()
    {
        var home = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        var applier = new SystemdUserApplier(TestDirs.NewTempDir(), new FakeAutostartProcessRunner());
        var lockDirectory = Path.Combine(home, WatchdPaths.DirName);
        Directory.CreateDirectory(lockDirectory);
        File.WriteAllText(Path.Combine(lockDirectory, WatchdPaths.LockFileName), "{ not json at all");

        var result = await RunInstallAsync(AutostartPlatform.SystemdUser, program, home, applier);

        result.Exit.ShouldBe(0);
        result.StdErr.ToString().ShouldBeEmpty();
    }

    // The read-before-apply order is the design, so it gets a pin a refactor cannot quietly step around:
    // this applier claims the real lock from inside InstallAsync, which is exactly what the entry does
    // once systemd brings it up. Install still says nothing, because it read the claim before applying
    // anything — move that read after the apply and this case goes red while the two above stay green.
    [Fact]
    public async Task Install_WhenTheEntryTakesTheLockAsItIsApplied_LeavesStderrEmpty()
    {
        var home = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        var unitDirectory = TestDirs.NewTempDir();
        using var applier = new LockTakingApplier(
            WatchdPaths.LockPathUnder(home),
            new SystemdUserApplier(unitDirectory, new FakeAutostartProcessRunner()));

        var result = await RunInstallAsync(AutostartPlatform.SystemdUser, program, home, applier);

        result.Exit.ShouldBe(0);
        result.StdErr.ToString().ShouldBeEmpty();
        result.StdOut.ToString().ShouldContain("watchd autostart installed");
        applier.Taken.ShouldBeTrue("the entry never claimed the lock, so this case measured nothing");
        Directory.GetFiles(unitDirectory, "*.service").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Install_OnTheWindowsBranch_HandsOverTheGeneratedLineAndAddsOnlyForce()
    {
        var program = TestProgram.Executable("squeezr.exe");
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);
        var line = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, program, WindowsHome);

        var result = await RunInstallAsync(AutostartPlatform.WindowsScheduledTask, program, WindowsHome, applier);

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
        var program = TestProgram.Executable("squeezr.exe");
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);

        var error = await Should.ThrowAsync<ArgumentException>(() => WatchdInstallCommand.ApplyAsync(
            AutostartPlatform.WindowsScheduledTask,
            WatchdUnitGenerator.LaunchPair(program),
            WindowsHomeWithQuote,
            applier,
            CancellationToken.None));

        error.ParamName.ShouldBe("squeezrHome");
        runner.Calls.ShouldBeEmpty();
    }

    // The guard now lives in the seam, so a caller that skips the command cannot skip it either: a
    // program path the init system would refuse to exec never reaches the generator, the applier or
    // the tool, and it leaves no half-written unit behind.
    [Fact]
    public async Task ApplyAsync_WhenTheProgramIsAbsent_RefusesBeforeWritingOrAskingTheTool()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var program = Path.Combine(TestDirs.NewTempDir(), "squeezr");

        var error = await Should.ThrowAsync<AutostartException>(() => WatchdInstallCommand.ApplyAsync(
            AutostartPlatform.SystemdUser,
            WatchdUnitGenerator.LaunchPair(program),
            Home,
            applier,
            CancellationToken.None));

        error.Message.ShouldContain("does not exist");
        error.Message.ShouldContain(RemedyFragment);
        Directory.GetFileSystemEntries(unitDirectory).ShouldBeEmpty();
        runner.Calls.ShouldBeEmpty();
    }

    [LinuxFact]
    public async Task ApplyAsync_WhenTheProgramCarriesNoExecuteBit_RefusesBeforeWritingOrAskingTheTool()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var program = TestProgram.NotExecutable("squeezr");

        var error = await Should.ThrowAsync<AutostartException>(() => WatchdInstallCommand.ApplyAsync(
            AutostartPlatform.SystemdUser,
            WatchdUnitGenerator.LaunchPair(program),
            Home,
            applier,
            CancellationToken.None));

        error.Message.ShouldContain("does not carry the execute bit");
        error.Message.ShouldContain(RemedyFragment);
        Directory.GetFileSystemEntries(unitDirectory).ShouldBeEmpty();
        runner.Calls.ShouldBeEmpty();
    }

    // T9 moved the assembly behind a muxer instead of leaving it in front of one, so the guard speaks
    // about the program token of the pair. What the operator is told has to match what the product now
    // does: naming "no muxer in front of it" as the defect would recommend against the very shape a
    // muxed install writes today (review-110-05 SUGGESTION-3).
    [LinuxFact]
    public async Task ApplyAsync_WhenTheProgramIsAManagedAssembly_RefusesAndNamesTheMuxerShape()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var assembly = TestProgram.Executable("squeezr.dll");

        var error = await Should.ThrowAsync<AutostartException>(() => WatchdInstallCommand.ApplyAsync(
            AutostartPlatform.SystemdUser,
            WatchdUnitGenerator.LaunchPair(assembly),
            Home,
            applier,
            CancellationToken.None));

        error.Message.ShouldContain("managed assembly");
        error.Message.ShouldContain(RemedyFragment);
        error.Message.ShouldContain("dotnet muxer");
        error.Message.ShouldNotContain(StaleRemedyFragment);
        Directory.GetFileSystemEntries(unitDirectory).ShouldBeEmpty();
        runner.Calls.ShouldBeEmpty();
    }

    // The payload is never exec'ed, so the guard has nothing to say about it: an assembly with no
    // execute bit behind a muxer that has one is exactly the unit a muxed install writes, and it has
    // to reach the disk rather than be refused as if the dll were the program.
    [LinuxFact]
    public async Task ApplyAsync_WhenThePairCarriesAnAssemblyPayload_GuardsOnlyTheProgram()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var muxer = TestProgram.Executable("dotnet");
        var payload = TestProgram.NotExecutable("squeezr.dll");

        var result = await RunInstallAsync(
            AutostartPlatform.SystemdUser,
            WatchdUnitGenerator.LaunchPair(muxer, payload),
            Home,
            applier);

        result.Exit.ShouldBe(0);
        var unit = Directory.GetFiles(unitDirectory, "*.service").ShouldHaveSingleItem();
        File.ReadAllText(unit)
            .ShouldContain($"""ExecStart="{muxer}" "{payload}" watchd --autostart""");
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

    // The same channel on the launchd side: a job that refuses to boot out keeps running until the
    // next logoff, and the operator reading "removed" with an empty stderr would never learn it.
    [Fact]
    public async Task Uninstall_Launchd_WhenBootoutRefusesToAnswer_RemovesThePlistSucceedsAndWarns()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("bootout")
            ? new AutostartProcessResult(5, string.Empty, "Boot-out failed: 5: Input/output error")
            : FakeAutostartProcessRunner.Ok);
        var applier = new LaunchAgentApplier(agentsDirectory, LaunchDomain, runner);
        var plist = applier.ManagedTarget;
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(plist, WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary, Home));

        var result = await RunUninstallAsync(applier);

        result.Exit.ShouldBe(0);
        File.Exists(plist).ShouldBeFalse();
        result.StdOut.ToString().ShouldContain("watchd autostart removed");
        result.StdErr.ToString().ShouldContain(
            "warning: 'launchctl' failed to boot out the launch agent (exit 5)");
        result.StdErr.ToString().ShouldContain("Boot-out failed: 5");
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
        RunInstallAsync(platform, WatchdUnitGenerator.LaunchPair(binaryPath), squeezrHome, applier);

    // The seam takes the launch pair install writes, so a test can hand over either launch shape: the
    // apphost alone, or the muxer with the assembly behind it.
    private static Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunInstallAsync(
        AutostartPlatform platform,
        WatchdRunCommand launch,
        string? squeezrHome,
        IWatchdAutostartApplier applier) =>
        RunUnitAsync(ct => WatchdInstallCommand.ApplyAsync(platform, launch, squeezrHome, applier, ct));

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
    // Two launch shapes matter and both are driven: the apphost is what an installed squeezr is, and
    // 'dotnet <assembly>' is how a repo build is run — since T9 the second one installs too, naming the
    // muxer as the program and the assembly as its argument instead of refusing.
    private static Task<(int ExitCode, string StdOut, string StdErr)> RunInstalledCliAsync(
        string configHome,
        string squeezrHome,
        string workingDir,
        string[] args,
        string? pathShimDirectory = null) =>
        RunCliAsync(AppHostPath(), null, configHome, squeezrHome, workingDir, args, pathShimDirectory);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunMuxedCliAsync(
        string configHome,
        string squeezrHome,
        string workingDir,
        string[] args,
        string? pathShimDirectory = null)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

        return RunCliAsync("dotnet", $"\"{cliDll}\"", configHome, squeezrHome, workingDir, args, pathShimDirectory);
    }

    private static string AppHostPath() =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "squeezr.exe" : "squeezr");

    // The unit the child wrote, read back from the temp config root install was pointed at.
    private static string ManagedUnitText(string configHome) =>
        File.ReadAllText(Path.Combine(configHome, "systemd", "user", UnitFileName));

    // A ledger that never appeared means the shim was never asked — and then the arm that reads it
    // would be green on an install that did nothing at all, so the absence is the failure, named.
    private static string[] ReadLedger(string ledgerPath)
    {
        File.Exists(ledgerPath).ShouldBeTrue(
            "install never asked systemctl anything, so the ledger arm measured nothing");
        return File.ReadAllLines(ledgerPath);
    }

    // The child must never reach a real bus: the shim answers every systemctl verb with success and is
    // placed first on the child's PATH, so the only thing a test can measure through it is what the
    // CLI did before or after asking the tool. With a ledger path it also records every argv it saw.
    private static string WriteSystemctlShim(string? ledgerPath = null)
    {
        var shimDirectory = TestDirs.NewTempDir();
        var shim = Path.Combine(shimDirectory, "systemctl");
        var record = ledgerPath is null
            ? string.Empty
            : $"printf '%s\\n' \"$*\" >> '{ledgerPath}'\n";
        File.WriteAllText(shim, $"#!/bin/sh\n{record}exit 0\n");
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

    // The retirement whole: a watchd the platform started has to leave on its own once the file it
    // runs from is replaced, because the entry — not the process — is what brings the new binary up.
    // Code, lock and operator line are measured on a real child: none of the three has an existence
    // one layer above, and the exit code is what Restart=on-failure actually keys on.
    [LinuxFact]
    public async Task Watchd_AutostartChild_RetiresAfterItsOwnBinaryIsReplaced()
    {
        var home = TestDirs.NewTempDir();
        var own = TakeOwnBinary();
        try
        {
            using var child = StartWatchdChild(home, "watchd", WatchdUnitGenerator.AutostartFlag);
            await child.HoldsTheLockAsync();

            UpgradeOwnBinary(own);
            var retired = await child.ExitedWithinAsync(TimeSpan.FromSeconds(30));

            retired.ShouldBeTrue("the autostarted watchd never retired after its binary was replaced");
            child.ExitCode.ShouldBe(75);
            File.Exists(child.LockPath).ShouldBeFalse("the retiring supervisor left its lock behind");
            var reason = await child.AwaitStdOutLineAsync(RetirementFragment, RetireLineBudget);
            reason.ShouldContain(own.Path);
            child.StdErr.ShouldBeEmpty();
        }
        finally
        {
            RestoreOwnBinary(own);
        }
    }

    // The manual path, argv tolerance included: a watchd somebody typed by hand never kills itself and
    // says nothing it did not say before this feature existed — the flag has to be the thing that
    // binds, not just any extra token on the command line.
    [LinuxFact]
    public async Task Watchd_ChildWithoutTheAutostartFlag_RunsOnThroughAnUpgrade()
    {
        var own = TakeOwnBinary();
        string[][] shapes = [["watchd"], ["watchd", "--bogus-option"]];

        try
        {
            foreach (var argv in shapes)
            {
                var home = TestDirs.NewTempDir();
                using var child = StartWatchdChild(home, argv);
                await child.HoldsTheLockAsync();

                UpgradeOwnBinary(own);
                await Task.Delay(SupervisorLoop.PollInterval * 4);

                child.HasExited.ShouldBeFalse($"'{string.Join(' ', argv)}' retired like an autostarted daemon");
                child.StdOut.ShouldBeEmpty();
                child.StdErr.ShouldBeEmpty();
            }
        }
        finally
        {
            RestoreOwnBinary(own);
        }
    }

    // The fleet half of the retirement: a supervisor that steps aside must not take its managed watch
    // daemon down with it, because until the entry brings the new binary up that daemon is the only
    // thing keeping the store indexed. A real workspace is registered in a pinned home, the supervisor
    // launches a real watch child and names its pid, and that pid has to outlive the exit 75.
    [LinuxFact]
    public async Task Watchd_AutostartChild_Retires_WhileItsManagedWatchDaemonKeepsRunning()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var own = TakeOwnBinary();
        int? managedPid = null;
        try
        {
            var init = await CliTestProcess.RunWithHomeAsync(home, workspace, "init", "--name", ManagedWorkspaceName, "--json");
            init.ExitCode.ShouldBe(0, init.StdErr);
            PointScratchConfigAtDeadQdrant(home, ManagedWorkspaceName);

            using var child = StartWatchdChild(home, "watchd", WatchdUnitGenerator.AutostartFlag);
            await child.HoldsTheLockAsync();
            managedPid = await child.ManagedWatchPidAsync(ManagedWorkspaceName);

            UpgradeOwnBinary(own);
            var retired = await child.ExitedWithinAsync(TimeSpan.FromSeconds(30));

            retired.ShouldBeTrue("the autostarted watchd never retired with a managed child running");
            child.ExitCode.ShouldBe(75);
            File.Exists(child.LockPath).ShouldBeFalse("the retiring supervisor left its lock behind");
            await child.AwaitStdOutLineAsync(RetirementFragment, RetireLineBudget);
            child.StdOut.ShouldNotContain("restarted watch");
            child.StdOut.ShouldNotContain("stopped watch");
            using var managed = Process.GetProcessById(managedPid.Value);
            managed.HasExited.ShouldBeFalse(
                $"the retire took watch daemon {managedPid} down with it; supervisor said: {child.StdOut}");
        }
        finally
        {
            RestoreOwnBinary(own);
            if (managedPid is { } pid)
            {
                StopManagedWatch(pid);
            }
        }
    }

    // The upgrade is simulated on the real file the child runs from, and only after that child is up —
    // the detector compares the file stamp against the process start. The stamp goes back afterwards:
    // that dll is the shared build output every other CLI child in the run launches, and a stamp left
    // ahead of a later process start reads as a pending upgrade to anything that compares it.
    private static (string Path, DateTime ModifiedUtc) TakeOwnBinary()
    {
        var path = CliTestProcess.CliAssemblyPath;
        return (path, File.GetLastWriteTimeUtc(path));
    }

    private static void UpgradeOwnBinary((string Path, DateTime ModifiedUtc) own) =>
        File.SetLastWriteTimeUtc(own.Path, DateTime.UtcNow);

    private static void RestoreOwnBinary((string Path, DateTime ModifiedUtc) own) =>
        File.SetLastWriteTimeUtc(own.Path, own.ModifiedUtc);

    private static WatchdChild StartWatchdChild(string squeezrHome, params string[] args) =>
        new(
            CliTestProcess.StartLiveChild(squeezrHome, TestDirs.NewTempDir(), args),
            Path.Combine(squeezrHome, WatchdPaths.DirName, WatchdPaths.LockFileName));

    // A scratch workspace that reaches a real Qdrant is a scratch workspace that writes where the
    // developer works. The config lives in the scratch home's store dir, so it is rewritten there: the
    // in-process path helpers resolve the suite's own home and would miss this one entirely.
    private static void PointScratchConfigAtDeadQdrant(string squeezrHome, string workspaceName)
    {
        var rows = JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(squeezrHome, SqueezrPaths.RegistryFileName)),
            SqueezrJson.FileOptions) ?? [];
        var storeKey = rows.Single(row => row.Name == workspaceName).StoreKey;
        var configPath = Path.Combine(
            squeezrHome,
            SqueezrPaths.WorkspacesDirName,
            storeKey,
            SqueezrPaths.ConfigFileName);
        var config = SemanticConfig.Bind(JsonSerializer.Deserialize<SqueezrConfig>(
            File.ReadAllText(configPath), SqueezrJson.FileOptions)!);
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                SemanticConfigFile.Attach(config.Core, config with { Qdrant = config.Qdrant with { Endpoint = TestWorkspaceConfig.DeadQdrantEndpoint } }),
                SqueezrJson.FileOptions));
    }

    // The case is precisely that the retire does not reap this child, so the harness owns the cleanup
    // the product deliberately skipped.
    private static void StopManagedWatch(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(15_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    // A daemon under test has to be watched while it runs, so both pipes drain into buffers instead of
    // being awaited to the end, and the harness stops the child even when an assertion throws.
    private sealed class WatchdChild : IDisposable
    {
        private static readonly TimeSpan Sweep = SupervisorLoop.PollInterval;

        private readonly Process _process;
        private readonly string _lockPath;
        private readonly object _gate = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();

        public WatchdChild(Process process, string lockPath)
        {
            _process = process;
            _lockPath = lockPath;
            process.OutputDataReceived += (_, e) => Append(_stdout, e.Data);
            process.ErrorDataReceived += (_, e) => Append(_stderr, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string LockPath => _lockPath;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public string StdOut
        {
            get
            {
                lock (_gate)
                {
                    return _stdout.ToString();
                }
            }
        }

        public string StdErr
        {
            get
            {
                lock (_gate)
                {
                    return _stderr.ToString();
                }
            }
        }

        public async Task HoldsTheLockAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_lockPath))
                {
                    return;
                }

                if (_process.HasExited)
                {
                    break;
                }

                await Task.Delay(200);
            }

            throw new InvalidOperationException(
                $"watchd never took {_lockPath} ({State()}); stdout: {StdOut}stderr: {StdErr}");
        }

        // The supervisor's own line is the witness that a child was managed at the retire moment — and
        // the denominator: without a pid read here the case would pass on a fleet that never existed.
        public async Task<int> ManagedWatchPidAsync(string workspaceName)
        {
            var marker = $"started watch {workspaceName} (pid ";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var line = StdOut.Split('\n').FirstOrDefault(candidate => candidate.Contains(marker));
                if (line is not null)
                {
                    var first = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                    var last = line.IndexOf(')', first);
                    if (last > first)
                    {
                        return int.Parse(line.AsSpan(first, last - first), CultureInfo.InvariantCulture);
                    }
                }

                if (_process.HasExited)
                {
                    break;
                }

                await Task.Delay(200);
            }

            throw new InvalidOperationException(
                $"the supervisor never reported a managed watch for '{workspaceName}' ({State()}); " +
                $"stdout: {StdOut}stderr: {StdErr}");
        }

        public async Task<bool> ExitedWithinAsync(TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    // Bounded on purpose. The parameterless WaitForExit also waits for the redirected
                    // pipes to reach EOF, and a managed watch grandchild inherits the write end of this
                    // child's stdout and keeps it open long after watchd is gone — EOF never comes.
                    _process.WaitForExit(5000);
                    return true;
                }

                await Task.Delay(200);
            }

            return _process.HasExited;
        }

        // The last lines a retiring supervisor wrote can still be in flight once its process is gone, and
        // with a live grandchild holding the pipe there is no EOF to wait for: poll the buffer instead.
        public async Task<string> AwaitStdOutLineAsync(string fragment, TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (true)
            {
                var line = StdOut.Split('\n').FirstOrDefault(candidate => candidate.Contains(fragment));
                if (line is not null)
                {
                    return line.Trim();
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"watchd stdout never carried '{fragment}' within {budget.TotalSeconds:F0}s; " +
                        $"stdout: {StdOut}stderr: {StdErr}");
                }

                await Task.Delay(200);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            _process.WaitForExit(10_000);
            _process.Dispose();
        }

        private string State() => _process.HasExited
            ? $"it exited with {_process.ExitCode}"
            : $"it is alive and well; the supervisor sweeps every {Sweep.TotalSeconds:F0} seconds";

        private void Append(StringBuilder target, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                target.AppendLine(line);
            }
        }
    }

    // Stands in for the instance the init system brings up: it takes the real lock at the moment the
    // entry is applied and holds it until the case is over, so the only thing a green run can mean is
    // that install looked at the lock before that moment.
    private sealed class LockTakingApplier(string lockPath, IWatchdAutostartApplier inner)
        : IWatchdAutostartApplier, IDisposable
    {
        private WatchdLock? _held;

        public bool Taken => _held is not null;

        public string ManagedTarget => inner.ManagedTarget;

        public Task InstallAsync(string unitText, CancellationToken cancellationToken)
        {
            _held = new WatchdLock(lockPath);
            _held.TryAcquire().Acquired.ShouldBeTrue("the fake entry could not claim its own lock");
            return inner.InstallAsync(unitText, cancellationToken);
        }

        public Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken) =>
            inner.UninstallAsync(cancellationToken);

        public Task<WatchdUnitProbe> ProbeAsync(CancellationToken cancellationToken) =>
            inner.ProbeAsync(cancellationToken);

        public void Dispose() => _held?.Dispose();
    }
}
