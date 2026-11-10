using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class SystemdUserApplierTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string UnitFileName = "squeezr-watchd.service";
    private const string Reload = "systemctl --user daemon-reload";
    private const string Enable = "systemctl --user enable --now " + UnitFileName;
    private const string Disable = "systemctl --user disable --now " + UnitFileName;

    [Fact]
    public async Task Install_CreatesTheDirectoryWritesTheGeneratedTextAndEnables()
    {
        var unitDirectory = Path.Combine(TestDirs.NewTempDir(), "systemd", "user");
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        var text = UnitText();

        await applier.InstallAsync(text, CancellationToken.None);

        File.ReadAllText(Path.Combine(unitDirectory, UnitFileName)).ShouldBe(text);
        runner.Commands().ShouldBe([Reload, Enable]);
        applier.ManagedTarget.ShouldBe(Path.Combine(unitDirectory, UnitFileName));
    }

    [Fact]
    public async Task Install_SecondTime_KeepsOneUnitFileAndCarriesTheNewText()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);

        await applier.InstallAsync(UnitText(), CancellationToken.None);
        await applier.InstallAsync(UnitText(Binary, "/var/tmp/other-home"), CancellationToken.None);

        Directory.GetFiles(unitDirectory, "*.service").ShouldHaveSingleItem();
        File.ReadAllText(Path.Combine(unitDirectory, UnitFileName)).ShouldBe(UnitText(Binary, "/var/tmp/other-home"));
        runner.Commands().ShouldBe([Reload, Enable, Reload, Enable]);
    }

    [Fact]
    public async Task Install_WhenReloadFails_ReportsTheToolAnswerAndNeverReachesEnable()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(1, string.Empty, "Failed to connect to bus: No such file or directory"));
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var error = await Should.ThrowAsync<AutostartException>(() => applier.InstallAsync(UnitText(), CancellationToken.None));

        error.Message.ShouldContain("failed to reload the systemd user manager (exit 1)");
        error.Message.ShouldContain("Failed to connect to bus");
        runner.Commands().ShouldBe([Reload]);
    }

    [Fact]
    public async Task Uninstall_DisablesDropsTheUnitAndReloads()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        await applier.InstallAsync(UnitText(), CancellationToken.None);
        runner.Calls.Clear();

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeTrue();
        removal.Warnings.ShouldBeEmpty();
        File.Exists(Path.Combine(unitDirectory, UnitFileName)).ShouldBeFalse();
        runner.Commands().ShouldBe([Disable, Reload]);
    }

    [Fact]
    public async Task Uninstall_WhenDisableRefusesToAnswer_StillDropsTheUnitAndCarriesItsAnswer()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("disable")
            ? new AutostartProcessResult(1, string.Empty, "Failed to connect to bus: No such file or directory")
            : FakeAutostartProcessRunner.Ok);
        var applier = new SystemdUserApplier(unitDirectory, runner);
        await applier.InstallAsync(UnitText(), CancellationToken.None);
        runner.Calls.Clear();

        var removal = await applier.UninstallAsync(CancellationToken.None);

        File.Exists(Path.Combine(unitDirectory, UnitFileName)).ShouldBeFalse();
        removal.WasRemoved.ShouldBeTrue();
        removal.Warnings.ShouldHaveSingleItem()
            .ShouldContain("'systemctl' failed to stop and unhook the watch daemon (exit 1)");
        removal.Warnings.Single().ShouldContain("Failed to connect to bus");
        runner.Commands().ShouldBe([Disable, Reload]);
    }

    [Fact]
    public async Task Uninstall_WhenDisableThrows_StillDropsTheUnitAndCarriesItsMessage()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new ThrowingDisableRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        await applier.InstallAsync(UnitText(), CancellationToken.None);

        var removal = await applier.UninstallAsync(CancellationToken.None);

        File.Exists(Path.Combine(unitDirectory, UnitFileName)).ShouldBeFalse();
        removal.WasRemoved.ShouldBeTrue();
        removal.Warnings.ShouldHaveSingleItem().ShouldContain("Could not start 'systemctl'");
    }

    [LinuxFact]
    public async Task Uninstall_WhenDisableRefusesToAnswer_StillDropsTheWantsLinkOfOurOwnUnit()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var link = WriteWantsLink(unitDirectory, "../" + UnitFileName);
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("disable")
            ? new AutostartProcessResult(1, string.Empty, "Failed to connect to bus: No such file or directory")
            : FakeAutostartProcessRunner.Ok);
        var applier = new SystemdUserApplier(unitDirectory, runner);
        await applier.InstallAsync(UnitText(), CancellationToken.None);

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeTrue();
        removal.Warnings.ShouldHaveSingleItem();
        File.Exists(link).ShouldBeFalse();
        File.Exists(Path.Combine(unitDirectory, UnitFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Uninstall_WhenReloadRefusesToAnswer_FailsCheckedAfterTheUnitIsGone()
    {
        var unitDirectory = TestDirs.NewTempDir();
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("daemon-reload")
            ? new AutostartProcessResult(1, string.Empty, "Failed to connect to bus: No such file or directory")
            : FakeAutostartProcessRunner.Ok);
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var error = await Should.ThrowAsync<AutostartException>(() => applier.UninstallAsync(CancellationToken.None));

        error.Message.ShouldContain("failed to reload the systemd user manager (exit 1)");
        File.Exists(Path.Combine(unitDirectory, UnitFileName)).ShouldBeFalse();
        runner.Commands().ShouldBe([Disable, Reload]);
    }

    [Fact]
    public async Task Uninstall_WhenNothingIsManaged_SaysSoAndRunsNoTool()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeFalse();
        removal.Warnings.ShouldBeEmpty();
        (await applier.ProbeAsync(CancellationToken.None)).Presence.ShouldBe(WatchdUnitAnswer.No);

        runner.Calls.ShouldBeEmpty();
        Directory.Exists(Path.Combine(unitDirectory, "default.target.wants")).ShouldBeFalse();
    }

    [LinuxFact]
    public async Task Uninstall_RemovesTheEnableLinkOfOurOwnUnit()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var link = WriteWantsLink(unitDirectory, "../" + UnitFileName);
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);
        await applier.InstallAsync(UnitText(), CancellationToken.None);
        runner.Calls.Clear();

        (await applier.UninstallAsync(CancellationToken.None)).WasRemoved.ShouldBeTrue();

        File.Exists(link).ShouldBeFalse();
        File.Exists(Path.Combine(unitDirectory, UnitFileName)).ShouldBeFalse();
    }

    [LinuxFact]
    public async Task Uninstall_RemovesADanglingLinkThatPointsAtOurOwnUnit()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var link = WriteWantsLink(unitDirectory, "../" + UnitFileName);
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);

        (await applier.UninstallAsync(CancellationToken.None)).WasRemoved.ShouldBeTrue();

        File.Exists(link).ShouldBeFalse();
        runner.Commands().ShouldBe([Reload]);
    }

    [LinuxFact]
    public async Task Uninstall_LeavesAWantsLinkThatDoesNotPointAtOurUnitAlone()
    {
        var unitDirectory = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(unitDirectory, "foreign.service"), "# not ours\n");
        var link = WriteWantsLink(unitDirectory, "../foreign.service");
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);

        (await applier.UninstallAsync(CancellationToken.None)).WasRemoved.ShouldBeFalse();

        File.Exists(link).ShouldBeTrue();
        new FileInfo(link).LinkTarget.ShouldBe("../foreign.service");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Probe_FollowsTheUnitFileAndCarriesItsText()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var applier = new SystemdUserApplier(unitDirectory, new FakeAutostartProcessRunner());

        (await applier.ProbeAsync(CancellationToken.None)).Presence.ShouldBe(WatchdUnitAnswer.No);
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());

        var probe = await applier.ProbeAsync(CancellationToken.None);
        probe.Presence.ShouldBe(WatchdUnitAnswer.Yes);
        probe.UnitText.ShouldBe(UnitText());
        probe.Enabled.ShouldBe(WatchdUnitAnswer.No);
    }

    [LinuxFact]
    public async Task Probe_WhenTheManagerReportsTheUnitFailed_SaysFailedAndKnowsTheUnit()
    {
        var unitDirectory = TestDirs.NewTempDir();
        WriteWantsLink(unitDirectory, "../" + UnitFileName);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(0, "failed\n", string.Empty));
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Failed.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Loaded.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Enabled.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Notes.ShouldBeEmpty();
        runner.Commands().ShouldBe([$"systemctl --user is-failed {UnitFileName}"]);
    }

    // Measured shape of a unit the file manager has on disk but the running manager has not read:
    // is-failed answers exit 4 with a state word, which is neither "failed" nor "healthy" — it is the
    // half-applied install, and the probe has to say so instead of letting the word read as an answer.
    [Fact]
    public async Task Probe_WhenTheManagerHasNotReadTheUnit_SaysItIsNotLoadedAndNamesTheExit()
    {
        var unitDirectory = TestDirs.NewTempDir();
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(4, "inactive\n", string.Empty));
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Failed.ShouldBe(WatchdUnitAnswer.No);
        probe.Loaded.ShouldBe(WatchdUnitAnswer.No);
        probe.Notes.ShouldContain(note =>
            note.Contains($"the running manager does not know {UnitFileName} (exit 4, answer 'inactive')"));
    }

    // The bus-down answer carries the same exit code as an answered "not failed" and differs only by
    // the silence on stdout: an exit code alone would call this a healthy unit.
    [Fact]
    public async Task Probe_WhenTheBusWillNotAnswer_SaysItCouldNotAskAndCarriesTheReason()
    {
        var unitDirectory = TestDirs.NewTempDir();
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            1,
            string.Empty,
            "Failed to connect to user scope bus via local transport: $DBUS_SESSION_BUS_ADDRESS not defined"));
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Failed.ShouldBe(WatchdUnitAnswer.Unknown);
        probe.Loaded.ShouldBe(WatchdUnitAnswer.Unknown);
        probe.Notes.ShouldContain(note => note.Contains("Failed to connect to user scope bus"));
    }

    [Fact]
    public async Task Probe_WhenSystemctlCannotBeStarted_SaysItCouldNotAskAndStillReadsTheDisk()
    {
        var unitDirectory = TestDirs.NewTempDir();
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());
        var runner = new UnstartableSystemctlRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Probed.ShouldBeTrue();
        probe.Presence.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Failed.ShouldBe(WatchdUnitAnswer.Unknown);
        probe.Notes.ShouldContain(note => note.Contains("could not be asked whether the unit failed"));
    }

    [LinuxFact]
    public async Task Probe_WhenTheWantsLinkIsOurs_SaysEnabled()
    {
        var unitDirectory = TestDirs.NewTempDir();
        WriteWantsLink(unitDirectory, "../" + UnitFileName);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(1, "active\n", string.Empty));
        var applier = new SystemdUserApplier(unitDirectory, runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Enabled.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Failed.ShouldBe(WatchdUnitAnswer.No);
        probe.Notes.ShouldBeEmpty();
    }

    [LinuxFact]
    public async Task Probe_WhenTheEnableLinkIsGoneButStale_SaysAbsentAndCarriesTheResidue()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var link = WriteWantsLink(unitDirectory, "../" + UnitFileName);
        var applier = new SystemdUserApplier(unitDirectory, new FakeAutostartProcessRunner());

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.No);
        probe.Notes.ShouldHaveSingleItem().ShouldContain("points at a unit file that is gone");
        File.Exists(link).ShouldBeTrue();
    }

    [LinuxFact]
    public async Task Probe_WhenTheUnitFileCannotBeRead_KeepsTheDiskFactsAndNamesTheRefusal()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var unit = Path.Combine(unitDirectory, UnitFileName);
        File.WriteAllText(unit, UnitText());
        SetOwnerMode(unit, UnixFileMode.UserWrite);

        var probe = await new SystemdUserApplier(unitDirectory, new FakeAutostartProcessRunner())
            .ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.Yes);
        probe.UnitText.ShouldBeNull();
        probe.Notes.ShouldContain(note => note.Contains("could not be read"));
    }

    [Fact]
    public async Task Install_WithACustomUnitName_ManagesThatNameOnly()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new SystemdUserApplier(unitDirectory, runner, "watchd-lane");

        await applier.InstallAsync(UnitText(unitName: "watchd-lane"), CancellationToken.None);

        File.Exists(Path.Combine(unitDirectory, "watchd-lane.service")).ShouldBeTrue();
        runner.Commands().ShouldBe([Reload, "systemctl --user enable --now watchd-lane.service"]);
    }

    private static string UnitText(
        string binaryPath = Binary,
        string? squeezrHome = Home,
        string? unitName = null) =>
        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, binaryPath, squeezrHome, unitName);

    // The mode rules are Linux file semantics — the test that leans on them is [LinuxFact], and the host
    // check here keeps the analyzer from calling the site reachable on a platform with no file mode.
    private static void SetOwnerMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static string WriteWantsLink(string unitDirectory, string target)
    {
        var wantsDirectory = Path.Combine(unitDirectory, "default.target.wants");
        Directory.CreateDirectory(wantsDirectory);
        var link = Path.Combine(wantsDirectory, UnitFileName);
        File.CreateSymbolicLink(link, target);
        return link;
    }

    // The answer a machine without a startable systemctl gives: not an exit code but a refusal from
    // the seam itself, raised only for the disable call so the reload still gets to speak.
    private sealed class ThrowingDisableRunner : IAutostartProcessRunner
    {
        public Task<AutostartProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            if (arguments.Contains("disable"))
            {
                throw new AutostartException($"Could not start '{fileName}'.");
            }

            return Task.FromResult(FakeAutostartProcessRunner.Ok);
        }

        public Task<AutostartProcessResult> RunRawAsync(
            string fileName,
            string argumentLine,
            CancellationToken cancellationToken) =>
            RunAsync(fileName, [argumentLine], cancellationToken);
    }

    // A host with no systemctl at all: the seam refuses before any process exists, which is a
    // different answer from a tool that ran and said something. The disk facts still have to come
    // through, and the failure question has to come back as "could not ask", never as "not failed".
    private sealed class UnstartableSystemctlRunner : IAutostartProcessRunner
    {
        public Task<AutostartProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            throw new AutostartException($"Could not start '{fileName}': No such file or directory");

        public Task<AutostartProcessResult> RunRawAsync(
            string fileName,
            string argumentLine,
            CancellationToken cancellationToken) =>
            RunAsync(fileName, [argumentLine], cancellationToken);
    }
}
