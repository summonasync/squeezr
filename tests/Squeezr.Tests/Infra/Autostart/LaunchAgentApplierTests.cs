using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class LaunchAgentApplierTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string Domain = "gui/502";
    // The applier's contract is to name the file and the service ref after the unit it was given, so
    // it reads the generator's own constant; the reverse-DNS string itself is pinned in the generator
    // snapshot, and a rename there is caught there, not by a second copy here.
    private const string Label = WatchdUnitGenerator.LaunchAgentUnitName;
    private const string PlistFileName = Label + ".plist";
    private const string Launchctl = "launchctl";

    [Fact]
    public async Task Install_WritesTheGeneratedPlistAndBootstrapsItIntoTheDomain()
    {
        var agentsDirectory = Path.Combine(TestDirs.NewTempDir(), "Library", "LaunchAgents");
        var runner = new FakeAutostartProcessRunner();
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        var text = PlistText();

        await applier.InstallAsync(text, CancellationToken.None);

        var plist = Path.Combine(agentsDirectory, PlistFileName);
        File.ReadAllText(plist).ShouldBe(text);
        runner.Commands().ShouldBe([$"{Launchctl} bootstrap {Domain} {plist}"]);
        applier.ManagedTarget.ShouldBe(plist);
    }

    [Fact]
    public async Task Install_SecondTime_TakesTheOldJobDownBeforeBootstrappingTheNewText()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        var plist = Path.Combine(agentsDirectory, PlistFileName);

        await applier.InstallAsync(PlistText(), CancellationToken.None);
        await applier.InstallAsync(PlistText(Binary, "/var/tmp/other-home"), CancellationToken.None);

        Directory.GetFiles(agentsDirectory, "*.plist").ShouldHaveSingleItem();
        File.ReadAllText(plist).ShouldContain("<string>/var/tmp/other-home</string>");
        runner.Commands().ShouldBe(
        [
            $"{Launchctl} bootstrap {Domain} {plist}",
            $"{Launchctl} bootout {Domain}/{Label}",
            $"{Launchctl} bootstrap {Domain} {plist}",
        ]);
    }

    [Fact]
    public async Task Install_WhenBootstrapFails_ReportsTheToolAnswer()
    {
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(1, string.Empty, "Bootstrap failed: 5: Input/output error"));
        var applier = new LaunchAgentApplier(TestDirs.NewTempDir(), Domain, runner);

        var error = await Should.ThrowAsync<AutostartException>(() => applier.InstallAsync(
            PlistText(), CancellationToken.None));

        error.Message.ShouldContain("failed to bootstrap the launch agent (exit 1)");
        error.Message.ShouldContain("Bootstrap failed: 5");
    }

    [Fact]
    public async Task Uninstall_WhenNoPlistIsManaged_SaysSoAndRunsNoTool()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);

        (await applier.UninstallAsync(CancellationToken.None)).WasRemoved.ShouldBeFalse();
        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeFalse();

        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Uninstall_BootsTheJobOutAndDropsThePlist()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        await applier.InstallAsync(PlistText(), CancellationToken.None);
        runner.Calls.Clear();

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeTrue();
        removal.Warnings.ShouldBeEmpty();
        File.Exists(Path.Combine(agentsDirectory, PlistFileName)).ShouldBeFalse();
        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeFalse();
        runner.Commands().ShouldBe([$"{Launchctl} bootout {Domain}/{Label}"]);
    }

    // The job was never bootstrapped, so launchctl answers "not found" — the plist still goes away and
    // the removal stands, but uninstall has no way to tell a never-loaded job from one it failed to
    // take down, so what the tool answered travels as a warning instead of being dropped.
    [Fact]
    public async Task Uninstall_DropsThePlistEvenWhenTheJobWasNeverBootstrapped()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(3, string.Empty, $"Could not find service {Label} in domain for user"));
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, PlistFileName), PlistText());

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeTrue();
        File.Exists(Path.Combine(agentsDirectory, PlistFileName)).ShouldBeFalse();
        removal.Warnings.ShouldHaveSingleItem()
            .ShouldContain($"'{Launchctl}' failed to boot out the launch agent (exit 3)");
        removal.Warnings.Single().ShouldContain("Could not find service");
    }

    [Fact]
    public async Task Uninstall_WhenBootoutRefusesToAnswer_DropsThePlistAndCarriesItsAnswer()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("bootout")
            ? new AutostartProcessResult(5, string.Empty, "Boot-out failed: 5: Input/output error")
            : FakeAutostartProcessRunner.Ok);
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        await applier.InstallAsync(PlistText(), CancellationToken.None);
        runner.Calls.Clear();

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeTrue();
        File.Exists(Path.Combine(agentsDirectory, PlistFileName)).ShouldBeFalse();
        removal.Warnings.ShouldHaveSingleItem().ShouldContain("Boot-out failed: 5");
        runner.Commands().ShouldBe([$"{Launchctl} bootout {Domain}/{Label}"]);
    }

    // Install keeps its bootout unchecked on purpose: over a plist that was written but never
    // bootstrapped, "unknown job" is the honest answer and the bootstrap right after is the step that
    // has to speak for itself.
    [Fact]
    public async Task Install_SecondTime_WhenBootoutReportsAnUnknownJob_StillBootstrapsTheNewText()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("bootout")
            ? new AutostartProcessResult(3, string.Empty, $"Could not find service {Label} in domain for user")
            : FakeAutostartProcessRunner.Ok);
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        await applier.InstallAsync(PlistText(), CancellationToken.None);

        await applier.InstallAsync(PlistText(Binary, "/var/tmp/other-home"), CancellationToken.None);

        var plist = Path.Combine(agentsDirectory, PlistFileName);
        File.ReadAllText(plist).ShouldContain("<string>/var/tmp/other-home</string>");
        runner.Commands().ShouldBe(
        [
            $"{Launchctl} bootstrap {Domain} {plist}",
            $"{Launchctl} bootout {Domain}/{Label}",
            $"{Launchctl} bootstrap {Domain} {plist}",
        ]);
    }

    [Fact]
    public async Task Install_WithACustomUnitName_ManagesThatLabelOnly()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner();
        var applier = new LaunchAgentApplier(agentsDirectory, "gui/1000", runner, "watchd-lane");

        await applier.InstallAsync(PlistText(unitName: "watchd-lane"), CancellationToken.None);

        File.Exists(Path.Combine(agentsDirectory, "watchd-lane.plist")).ShouldBeTrue();
        runner.Commands().ShouldBe([$"{Launchctl} bootstrap gui/1000 {Path.Combine(agentsDirectory, "watchd-lane.plist")}"]);
    }

    private static string PlistText(
        string binaryPath = Binary,
        string? squeezrHome = Home,
        string? unitName = null) =>
        WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, binaryPath, squeezrHome, unitName);
}
