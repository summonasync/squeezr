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
        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeFalse();

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
    public async Task IsInstalled_FollowsTheUnitFile()
    {
        var unitDirectory = TestDirs.NewTempDir();
        var applier = new SystemdUserApplier(unitDirectory, new FakeAutostartProcessRunner());

        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeFalse();
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(Path.Combine(unitDirectory, UnitFileName), UnitText());

        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeTrue();
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
}
