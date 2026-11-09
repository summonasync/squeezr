using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class LaunchAgentApplierTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string Domain = "gui/502";
    private const string PlistFileName = "squeezr-watchd.plist";
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
            $"{Launchctl} bootout {Domain}/squeezr-watchd",
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

        (await applier.UninstallAsync(CancellationToken.None)).ShouldBeFalse();
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

        (await applier.UninstallAsync(CancellationToken.None)).ShouldBeTrue();

        File.Exists(Path.Combine(agentsDirectory, PlistFileName)).ShouldBeFalse();
        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeFalse();
        runner.Commands().ShouldBe([$"{Launchctl} bootout {Domain}/squeezr-watchd"]);
    }

    [Fact]
    public async Task Uninstall_DropsThePlistEvenWhenTheJobWasNeverBootstrapped()
    {
        var agentsDirectory = TestDirs.NewTempDir();
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(3, string.Empty, "Could not find service squeezr-watchd in domain for user"));
        var applier = new LaunchAgentApplier(agentsDirectory, Domain, runner);
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, PlistFileName), PlistText());

        (await applier.UninstallAsync(CancellationToken.None)).ShouldBeTrue();

        File.Exists(Path.Combine(agentsDirectory, PlistFileName)).ShouldBeFalse();
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
