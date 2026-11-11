using System.Diagnostics;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Storage;
using Squeezr.Tests.Infra.Autostart;

namespace Squeezr.Tests.Cli;

// Every state the contract names is measured here as its own case, because the whole point of the
// command is that the states are not collapsed into one another: a failed unit and a missing unit are
// different facts, and a probe that could not ask is a third.
[Collection("ProcessGlobals")]
public sealed class WatchdInstalledCliTests
{
    private const string Target = "/managed/squeezr-watchd";
    private const string Gone = "/opt/squeezr/2.0.0/squeezr";
    private const string InstallHint = "squeezr watchd install";

    [Fact]
    public async Task Installed_WhenTheEntryIsHookedAndItsProgramLaunchable_SaysInstalledAndExitsZero()
    {
        var program = TestProgram.Executable("squeezr");
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.Yes,
            UnitOnSystemd(program)));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.Healthy);
        Lines(result.Out).ShouldBe(
        [
            "status=installed",
            "platform=systemd-user",
            $"unit={Target}",
            "present=yes",
            "enabled=yes",
            "failed=no",
            "loaded=yes",
            $"program={program}",
            "arguments=watchd --autostart",
            "program-state=launchable",
            "watchd=not-running",
        ]);
        result.Err.ShouldBeEmpty();
    }

    [Fact]
    public async Task Installed_WhenNothingIsRegistered_AnswersNotInstalledWithoutAStackTrace()
    {
        var result = await RunAsync(Applier(WatchdUnitProbe.Absent(AutostartPlatform.SystemdUser)));

        result.Exit.ShouldBe(WatchdInstalledVerdict.NotRegistered);
        Lines(result.Out).ShouldContain("status=not-installed");
        Lines(result.Out).ShouldContain("present=no");
        // Nothing is registered, so there is no entry whose failure could be asked about: the answer
        // this dimension carries is "no such question here", not a question this probe lost.
        Lines(result.Out).ShouldContain("failed=not-supported");
        Lines(result.Out).ShouldContain(line => line.StartsWith("hint=", StringComparison.Ordinal) &&
                                                line.Contains(InstallHint, StringComparison.Ordinal));
        result.Out.ShouldNotContain("at Squeezr.");
        result.Err.ShouldBeEmpty();
    }

    // Spec decision 3: the entry survives the upgrade that deleted its directory, and the diagnostic
    // has to name that path rather than restate what install would write today.
    [Fact]
    public async Task Installed_WhenTheProgramTheEntryNamesIsGone_SaysBinaryMissingAndHintsReinstall()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.Yes,
            UnitOnSystemd(Gone)));
        File.Exists(Gone).ShouldBeFalse();

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=installed-but-binary-missing");
        Lines(result.Out).ShouldContain($"program={Gone}");
        Lines(result.Out).ShouldContain("program-state=does not exist");
        Lines(result.Out).ShouldContain(line => line.StartsWith("hint=", StringComparison.Ordinal) &&
                                                line.Contains(InstallHint, StringComparison.Ordinal));
    }

    // The T9 shape read back: the entry can be sound about its program and broken about the payload it
    // loads. systemd starts the muxer, the muxer refuses a missing assembly and exits non-zero — so a
    // reader that only looked at the first token would call this 'installed' while the unit crash-loops.
    [Fact]
    public async Task Installed_WhenTheMuxerIsThereButTheAssemblyItLoadsIsGone_SaysBinaryMissing()
    {
        var muxer = TestProgram.Executable("dotnet");
        var gone = Path.Combine(TestDirs.NewTempDir(), "2.0.0", "squeezr.dll");
        var unit = WatchdUnitGenerator.Generate(
            AutostartPlatform.SystemdUser, WatchdUnitGenerator.LaunchPair(muxer, gone));
        File.Exists(gone).ShouldBeFalse();
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.Yes, unit));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=installed-but-binary-missing");
        Lines(result.Out).ShouldContain($"program={muxer}");
        Lines(result.Out).ShouldContain($"arguments={gone} watchd --autostart");
        // The complaint names the payload, not the head: the muxer passed the same launchability rules
        // install applies, so the missing assembly is the only thing this entry can be broken about.
        Lines(result.Out).ShouldContain(
            $"program-state=the path '{gone}' the entry passes to the program does not exist");
        Lines(result.Out).ShouldContain(line => line.StartsWith("hint=", StringComparison.Ordinal) &&
                                                line.Contains(InstallHint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installed_WhenTheManagerReportsTheEntryFailed_SaysUnitFailedAndNotMissing()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes,
            UnitOnSystemd(TestProgram.Executable("squeezr"))));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=unit-failed");
        Lines(result.Out).ShouldContain("present=yes");
        Lines(result.Out).ShouldContain("failed=yes");
    }

    // The read-side half of BLOCKER-2: a unit file that exists with no enable hook is not installed,
    // and reporting it as installed would repeat the same lie with the signs flipped.
    [Fact]
    public async Task Installed_WhenTheEntryIsOnDiskButNotHooked_SaysPresentButNotEnabled()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.No, WatchdUnitAnswer.No,
            UnitOnSystemd(TestProgram.Executable("squeezr"))));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=present-but-not-enabled");
        Lines(result.Out).ShouldContain("enabled=no");
        Lines(result.Out).ShouldContain(line => line.Contains("half applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installed_WhenThePlatformRefusedToAnswer_SaysProbeUnknownAndCarriesTheReason()
    {
        var applier = Applier(new WatchdUnitProbe(
            AutostartPlatform.WindowsScheduledTask,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unknown,
            null,
            [
                "'schtasks.exe' refused to answer (exit 1): ERROR: Access is denied.",
            ]));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.NoVerdict);
        Lines(result.Out).ShouldContain("status=probe-unknown");
        Lines(result.Out).ShouldContain("platform=windows-task");
        Lines(result.Out).ShouldContain("present=couldn't-ask");
        Lines(result.Out).ShouldContain(line => line.Contains("Access is denied.", StringComparison.Ordinal));
    }

    // Everything on disk says installed and the one open question is whether the unit is sitting in the
    // crash-loop failure state: a platform that asks that question and does not answer it cannot be
    // reported as healthy.
    [Fact]
    public async Task Installed_WhenTheFailureQuestionIsOpen_RefusesToCallTheEntryHealthy()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.Unknown, WatchdUnitAnswer.Unknown,
            UnitOnSystemd(TestProgram.Executable("squeezr")),
            "systemd could not be asked whether the unit failed: Failed to connect to bus"));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.NoVerdict);
        Lines(result.Out).ShouldContain("status=probe-unknown");
        Lines(result.Out).ShouldContain("failed=couldn't-ask");
    }

    // The enablement question is asked on more than one platform, and an answer that never came back is
    // an open question on all of them: the half-hooked entry this command exists to name is exactly the
    // one an unread flag cannot be allowed to hide behind. Same rung as the failure rule, same exit.
    [Theory]
    [InlineData("<Enabled>maybe</Enabled>")]
    [InlineData("")]
    public async Task Installed_OnTheWindowsApplier_WhenTheEnablementFlagSaysNothing_LeavesTheVerdictOpen(string flag)
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml(
                $"/c set \"SQUEEZR_HOME=D:\\squeezr home\" && \"{TestProgram.Executable("squeezr.exe")}\" watchd --autostart")
                .Replace("<Enabled>true</Enabled>", flag),
            string.Empty));

        var result = await RunAsync(new WindowsScheduledTaskApplier(runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.NoVerdict);
        Lines(result.Out).ShouldContain("status=probe-unknown");
        Lines(result.Out).ShouldContain("enabled=couldn't-ask");
    }

    // The schema types the flag as xs:boolean, so the digit spelling is a legal byte in a real export
    // and it says the same thing the word does: a disabled task reads disabled, never healthy.
    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    public async Task Installed_OnTheWindowsApplier_EitherSpellingOfADisabledTask_SaysPresentButNotEnabled(string flag)
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml(
                $"/c set \"SQUEEZR_HOME=D:\\squeezr home\" && \"{TestProgram.Executable("squeezr.exe")}\" watchd --autostart",
                flag),
            string.Empty));

        var result = await RunAsync(new WindowsScheduledTaskApplier(runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=present-but-not-enabled");
        Lines(result.Out).ShouldContain("enabled=no");
    }

    [Fact]
    public async Task Installed_WhenThePlatformHasNoProbeToRun_SaysProbeUnsupportedInsteadOfInstalled()
    {
        var applier = Applier(WatchdUnitProbe.Unprobeable(
            AutostartPlatform.WindowsScheduledTask,
            "schtasks could not be asked about the task: Could not start 'schtasks.exe'."));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.NoVerdict);
        Lines(result.Out).ShouldContain("status=probe-unsupported");
        Lines(result.Out).ShouldContain("platform=windows-task");
        Lines(result.Out).ShouldContain("enabled=not-supported");
        Lines(result.Out).ShouldContain(line => line.Contains("Could not start 'schtasks.exe'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installed_WhenTheEntryNamesNoCommand_SaysTheVerdictCouldNotBeRead()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.Yes,
            "[Service]\nType=simple\nRestart=on-failure\n"));

        var result = await RunAsync(applier);

        result.Exit.ShouldBe(WatchdInstalledVerdict.NoVerdict);
        Lines(result.Out).ShouldContain("status=probe-unknown");
        Lines(result.Out).ShouldContain("note=the entry carries no command this reader could identify");
        Lines(result.Out).ShouldNotContain(line => line.StartsWith("program=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installed_NamesTheLiveSupervisorAndItsPid()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.Yes,
            UnitOnSystemd(TestProgram.Executable("squeezr"))));

        var result = await RunAsync(applier, new FleetWatchdState(true, 4242));

        Lines(result.Out).ShouldContain("watchd=running");
        Lines(result.Out).ShouldContain("pid=4242");
    }

    [Fact]
    public async Task Installed_PrintsPlainKeyValuesWithNoAnsiMarkup()
    {
        var applier = Applier(Entry(
            WatchdUnitAnswer.Yes, WatchdUnitAnswer.No, WatchdUnitAnswer.No, WatchdUnitAnswer.No,
            UnitOnSystemd("/opt/sq/[bracketed] dir/squeezr")));

        var result = await RunAsync(applier);
        var lines = Lines(result.Out);

        result.Out.ShouldNotContain("\u001b");
        lines.ShouldAllBe(line => System.Text.RegularExpressions.Regex.IsMatch(line, "^[a-z-]+="));
        lines.ShouldContain("program=/opt/sq/[bracketed] dir/squeezr");
    }

    [Theory]
    [InlineData(WatchdAutostartState.Installed, WatchdInstalledVerdict.Healthy)]
    [InlineData(WatchdAutostartState.NotInstalled, WatchdInstalledVerdict.NotRegistered)]
    [InlineData(WatchdAutostartState.InstalledButBinaryMissing, WatchdInstalledVerdict.Broken)]
    [InlineData(WatchdAutostartState.UnitFailed, WatchdInstalledVerdict.Broken)]
    [InlineData(WatchdAutostartState.PresentButNotEnabled, WatchdInstalledVerdict.Broken)]
    [InlineData(WatchdAutostartState.ProbeUnknown, WatchdInstalledVerdict.NoVerdict)]
    [InlineData(WatchdAutostartState.ProbeUnsupported, WatchdInstalledVerdict.NoVerdict)]
    public void ExitCode_EveryState_MatchesTheDocumentedLadder(WatchdAutostartState state, int expected) =>
        WatchdInstalledVerdict.ExitCodeFor(state).ShouldBe(expected);

    [Fact]
    public void StateKeys_EveryState_NamesItselfStablyAndDifferently()
    {
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.Installed).ShouldBe("installed");
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.NotInstalled).ShouldBe("not-installed");
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.InstalledButBinaryMissing)
            .ShouldBe("installed-but-binary-missing");
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.UnitFailed).ShouldBe("unit-failed");
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.PresentButNotEnabled)
            .ShouldBe("present-but-not-enabled");
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.ProbeUnknown).ShouldBe("probe-unknown");
        WatchdInstalledVerdict.StateKey(WatchdAutostartState.ProbeUnsupported).ShouldBe("probe-unsupported");

        Enum.GetValues<WatchdAutostartState>()
            .Select(WatchdInstalledVerdict.StateKey)
            .Distinct()
            .Count()
            .ShouldBe(Enum.GetValues<WatchdAutostartState>().Length);
    }

    // The whole chain on the real applier: a unit file install wrote, with the enable link missing
    // because the run died before or during enable. Nothing here is a fake answer — the fake is only
    // the systemctl that has to be asked.
    [Fact]
    public async Task Installed_OnTheSystemdApplier_ReadsAHalfAppliedInstallOutOfTheDiskAndOneToolCall()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        File.WriteAllText(
            Path.Combine(unitDirectory, "squeezr-watchd.service"),
            UnitOnSystemd(program));
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(1, "active\n", string.Empty));

        var result = await RunAsync(new SystemdUserApplier(unitDirectory, runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=present-but-not-enabled");
        Lines(result.Out).ShouldContain("loaded=yes");
        Lines(result.Out).ShouldContain($"program={program}");
        runner.Commands().ShouldBe(["systemctl --user is-failed squeezr-watchd.service"]);
    }

    // The exit-4 answer carried all the way to the printed lines: the manager named a state of a unit it
    // has never read, so the failure dimension has no answer to print and says so, while the half-applied
    // install still reads as the state an operator can act on.
    [Fact]
    public async Task Installed_OnTheSystemdApplier_WhenTheManagerHasNotReadTheUnit_PrintsThatItDidNotAnswer()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        File.WriteAllText(
            Path.Combine(unitDirectory, "squeezr-watchd.service"),
            UnitOnSystemd(program));
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(4, "inactive\n", string.Empty));

        var result = await RunAsync(new SystemdUserApplier(unitDirectory, runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=present-but-not-enabled");
        Lines(result.Out).ShouldContain("failed=not-supported");
        Lines(result.Out).ShouldContain("loaded=no");
        Lines(result.Out).ShouldContain(line =>
            line.Contains("the running manager does not know squeezr-watchd.service (exit 4, answer 'inactive')",
                StringComparison.Ordinal));
    }

    // The other half of the exit-4 reading, measured rather than argued: a unit that is on disk AND
    // hooked, but the running manager has never read it. The verdict rides on the three facts the
    // platform did answer (present, hooked, launchable program), while the failure dimension says
    // plainly that this answer was not given — and a unit the manager never loaded also never failed,
    // so exit 4 hides no crash report the healthy arm would be swallowing.
    [LinuxFact]
    public async Task Installed_OnTheSystemdApplier_WhenAHookedUnitIsNotLoaded_KeepsTheVerdictOnTheAnsweredFacts()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        File.WriteAllText(
            Path.Combine(unitDirectory, "squeezr-watchd.service"),
            UnitOnSystemd(program));
        Directory.CreateDirectory(Path.Combine(unitDirectory, "default.target.wants"));
        File.CreateSymbolicLink(
            Path.Combine(unitDirectory, "default.target.wants", "squeezr-watchd.service"),
            "../squeezr-watchd.service");
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(4, "inactive\n", string.Empty));

        var result = await RunAsync(new SystemdUserApplier(unitDirectory, runner));

        Lines(result.Out).ShouldContain("enabled=yes");
        Lines(result.Out).ShouldContain("failed=not-supported");
        Lines(result.Out).ShouldContain("loaded=no");
        Lines(result.Out).ShouldContain("status=installed");
        result.Exit.ShouldBe(WatchdInstalledVerdict.Healthy);
    }

    // Why the failure answer "not-supported" stays non-blocking, pinned rather than argued: this
    // platform has no machine-readable failure question at all, and its entry answers that way on every
    // run. Turn the unanswered shape into a verdict-blocked one and a healthy agent stops being
    // reportable here — the same lie with the sign flipped.
    [Fact]
    public async Task Installed_OnTheLaunchAgentApplier_WhereFailureIsNeverAnAnswer_StillReportsTheEntry()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var program = TestProgram.Executable("squeezr");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(
            Path.Combine(agentsDirectory, "dev.summonasync.squeezr-watchd.plist"),
            WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, program));
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(0, "Guiv2 GUI\n", string.Empty));

        var result = await RunAsync(new LaunchAgentApplier(agentsDirectory, "gui/1000", runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.Healthy);
        Lines(result.Out).ShouldContain("status=installed");
        Lines(result.Out).ShouldContain("failed=not-supported");
        Lines(result.Out).ShouldContain("enabled=yes");
    }

    [Fact]
    public async Task Installed_OnTheWindowsApplier_ReadsTheProgramOutOfTheTaskXml()
    {
        var program = TestProgram.Executable("squeezr.exe");
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml($"/c set \"SQUEEZR_HOME=D:\\squeezr home\" && \"{program}\" watchd --autostart"),
            string.Empty));

        var result = await RunAsync(new WindowsScheduledTaskApplier(runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.Healthy);
        Lines(result.Out).ShouldContain("status=installed");
        Lines(result.Out).ShouldContain($"program={program}");
        Lines(result.Out).ShouldContain("enabled=yes");
        Lines(result.Out).ShouldContain("failed=no");
    }

    [Fact]
    public async Task Installed_OnTheWindowsApplier_WhenTheStoredProgramIsGone_SaysBinaryMissing()
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml($"/c set \"SQUEEZR_HOME=D:\\squeezr home\" && \"{Gone}.exe\" watchd --autostart"),
            string.Empty));

        var result = await RunAsync(new WindowsScheduledTaskApplier(runner));

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=installed-but-binary-missing");
        Lines(result.Out).ShouldContain($"program={Gone}.exe");
    }

    // The write-side half of MINOR-5: a store that will not say whether its task exists leaves the task
    // exactly where it was, and uninstall must not read as finished.
    [Fact]
    public async Task Uninstall_WhenSchtasksWouldNotSay_ReportsItCouldNotConfirmAndFails()
    {
        var applier = new FakeWatchdAutostartApplier(
            WatchdUnitProbe.Absent(AutostartPlatform.WindowsScheduledTask))
        {
            Removal = AutostartRemoval.Unconfirmed(
                "schtasks would not say whether the task exists, so nothing was deleted (exit 1): ERROR: Access is denied."),
        };

        var result = await RunRemovalAsync(applier);

        result.Exit.ShouldBe(1);
        result.Err.ShouldContain("warning: schtasks would not say whether the task exists");
        result.Err.ShouldContain("error: nothing was removed");
        result.Err.ShouldContain("could not be confirmed");
        result.Err.ShouldNotContain("nothing was removed.");
        result.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task WatchdHelp_Child_ListsAllThreeAutostartVerbs()
    {
        var result = await CliTestProcess.RunAsync(TestDirs.NewTempDir(), "watchd", "--help");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("install");
        result.StdOut.ShouldContain("uninstall");
        result.StdOut.ShouldContain("installed");
    }

    // A live child run of the verb itself: the platform tool is a shim on PATH, so what is measured is
    // that an unregistered machine gets a clean answer and that not one tool call is made — a status
    // command that asks systemctl to do nothing is still a status command that touched the bus.
    [LinuxFact]
    public async Task Installed_Child_WhenNothingIsRegistered_AnswersNotInstalledAndAsksNoTool()
    {
        var configHome = TestDirs.NewTempDir();
        var ledger = Path.Combine(TestDirs.NewTempDir(), "systemctl.ledger");
        var shim = WriteSystemctlShim(ledger);

        var result = await RunChildAsync(configHome, TestDirs.NewTempDir(), shim, ledger, "watchd", "installed");

        result.Exit.ShouldBe(WatchdInstalledVerdict.NotRegistered);
        Lines(result.Out).ShouldContain("status=not-installed");
        File.Exists(ledger).ShouldBeFalse();
    }

    // The same run with an entry that points at a deleted directory: the answer has to name the broken
    // path, and the ledger has to prove the command asked systemd one read-only question and did not
    // enable, start, stop, reload or install anything on the way past.
    [LinuxFact]
    public async Task Installed_Child_WhenTheUnitNamesADeletedProgram_ReportsItAndRunsNoManagingVerb()
    {
        var configHome = TestDirs.NewTempDir();
        var unitDirectory = Path.Combine(configHome, "systemd", "user");
        Directory.CreateDirectory(Path.Combine(unitDirectory, "default.target.wants"));
        File.WriteAllText(Path.Combine(unitDirectory, "squeezr-watchd.service"), UnitOnSystemd(Gone));
        File.CreateSymbolicLink(
            Path.Combine(unitDirectory, "default.target.wants", "squeezr-watchd.service"),
            "../squeezr-watchd.service");
        var ledger = Path.Combine(TestDirs.NewTempDir(), "systemctl.ledger");
        var shim = WriteSystemctlShim(ledger);

        var result = await RunChildAsync(configHome, TestDirs.NewTempDir(), shim, ledger, "watchd", "installed");

        result.Exit.ShouldBe(WatchdInstalledVerdict.Broken);
        Lines(result.Out).ShouldContain("status=installed-but-binary-missing");
        Lines(result.Out).ShouldContain($"unit={Path.Combine(unitDirectory, "squeezr-watchd.service")}");
        File.ReadAllText(ledger).Trim().ShouldBe("--user is-failed squeezr-watchd.service");
    }

    private static WatchdUnitProbe Entry(
        WatchdUnitAnswer presence,
        WatchdUnitAnswer enabled,
        WatchdUnitAnswer failed,
        WatchdUnitAnswer loaded,
        string? unitText,
        params string[] notes) =>
        new(AutostartPlatform.SystemdUser, presence, enabled, failed, loaded, unitText, [.. notes]);

    private static FakeWatchdAutostartApplier Applier(WatchdUnitProbe probe, string target = Target) =>
        new(probe, target);

    private static string UnitOnSystemd(string programPath) =>
        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, programPath);

    private static string TaskXml(string cmdArguments, string enabled = "true") =>
        $"""
        <Task version="1.2">
          <Settings><Enabled>{enabled}</Enabled></Settings>
          <Actions><Exec><Command>cmd.exe</Command><Arguments>{XmlText(cmdArguments)}</Arguments></Exec></Actions>
          <RuntimeInfo><LastTaskResult>0</LastTaskResult></RuntimeInfo>
        </Task>
        """;

    private static string XmlText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static Task<(int Exit, string Out, string Err)> RunAsync(
        IWatchdAutostartApplier applier,
        FleetWatchdState? watchd = null) =>
        CaptureAsync(ct => WatchdInstalledCommand.ReportAsync(
            applier,
            watchd ?? new FleetWatchdState(false, null),
            ct));

    private static Task<(int Exit, string Out, string Err)> RunRemovalAsync(IWatchdAutostartApplier applier) =>
        CaptureAsync(ct => WatchdUninstallCommand.RemoveAsync(applier, ct));

    private static async Task<(int Exit, string Out, string Err)> CaptureAsync(
        Func<CancellationToken, Task<int>> run)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await run(CancellationToken.None);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private static string[] Lines(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // The shim answers is-failed with the exit code and the state word measured on systemd 255 for a
    // loaded unit that has not failed, and it writes every argv it saw so a test can prove the
    // diagnostic asked nothing that manages anything. It sits first on the child's PATH, so the real
    // user bus is never reached.
    private static string WriteSystemctlShim(string ledgerPath)
    {
        var shimDirectory = TestDirs.NewTempDir();
        var shim = Path.Combine(shimDirectory, "systemctl");
        File.WriteAllText(shim, $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> '{{ledgerPath}}'
            for arg in "$@"; do
              if [ "$arg" = "is-failed" ]; then
                echo inactive
                exit 1
              fi
            done
            exit 0
            """);
        SetOwnerExecutable(shim);
        return shimDirectory;
    }

    // The mode rules are Linux file semantics — the tests that lean on them are [LinuxFact], and the
    // host check here keeps the analyzer from calling these sites platform-reachable.
    private static void SetOwnerExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static async Task<(int Exit, string Out, string Err)> RunChildAsync(
        string configHome,
        string storeHome,
        string shimDirectory,
        string ledgerPath,
        params string[] args)
    {
        var startInfo = new ProcessStartInfo(AppHostPath(), string.Join(' ', args))
        {
            WorkingDirectory = TestDirs.NewTempDir(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["SQUEEZR_CLIENT_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["DOTNET_CLI_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["XDG_CONFIG_HOME"] = configHome;
        startInfo.Environment[SqueezrHome.HomeEnvironmentVariable] = storeHome;
        startInfo.Environment["SYSTEMCTL_LEDGER"] = ledgerPath;
        var inherited = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inherited)
            ? shimDirectory
            : $"{shimDirectory}{Path.PathSeparator}{inherited}";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string AppHostPath() =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "squeezr.exe" : "squeezr");
}
