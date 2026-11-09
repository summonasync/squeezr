using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class WindowsScheduledTaskApplierTests
{
    private const string Binary = @"C:\Program Files\Squeezr\squeezr.exe";
    private const string Home = @"D:\squeezr home";
    private const string Schtasks = "schtasks.exe";

    [Fact]
    public async Task Install_HandsTheGeneratedLineOverVerbatimAndAddsForce()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);
        var line = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, Binary, Home);

        await applier.InstallAsync(line, CancellationToken.None);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.Verbatim.ShouldBeTrue();
        call.FileName.ShouldBe(Schtasks);
        call.Arguments.ShouldHaveSingleItem().ShouldBe(line + " /F");
        call.Arguments.Single().ShouldContain("""
            /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """);
    }

    [Fact]
    public async Task Install_WithoutSqueezrHome_InventsNoCmdWrapper()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);
        var line = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, Binary);

        await applier.InstallAsync(line, CancellationToken.None);

        var commandLine = runner.Calls.Single().Arguments.Single();
        commandLine.ShouldBe(line + " /F");
        commandLine.ShouldNotContain("SQUEEZR_HOME");
    }

    [Fact]
    public async Task Install_SecondTime_RewritesTheSameTaskThroughForceAlone()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);
        var line = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, Binary, Home);

        await applier.InstallAsync(line, CancellationToken.None);
        await applier.InstallAsync(line, CancellationToken.None);

        runner.Calls.Count.ShouldBe(2);
        runner.Calls.ShouldAllBe(call => call.Arguments.Single().EndsWith(" /F"));
    }

    [Fact]
    public async Task Install_WhenSchtasksRefuses_ReportsTheToolAnswer()
    {
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(1, string.Empty, "ERROR: Access is denied."));
        var applier = new WindowsScheduledTaskApplier(runner);

        var error = await Should.ThrowAsync<AutostartException>(() => applier.InstallAsync(
            WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, Binary),
            CancellationToken.None));

        error.Message.ShouldContain("failed to create the scheduled task (exit 1)");
        error.Message.ShouldContain("Access is denied.");
    }

    [Fact]
    public async Task Uninstall_WhenTheTaskIsAbsent_DeletesNothing()
    {
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("/Query")
            ? new AutostartProcessResult(1, string.Empty, "ERROR: The system cannot find the file specified.")
            : FakeAutostartProcessRunner.Ok);
        var applier = new WindowsScheduledTaskApplier(runner);

        (await applier.UninstallAsync(CancellationToken.None)).ShouldBeFalse();

        runner.Commands().ShouldBe(["schtasks.exe /Query /TN squeezr-watchd"]);
    }

    [Fact]
    public async Task IsInstalled_WhenTheTaskIsAbsent_IsFalse()
    {
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("/Query")
            ? new AutostartProcessResult(1, string.Empty, "ERROR: The system cannot find the file specified.")
            : FakeAutostartProcessRunner.Ok);
        var applier = new WindowsScheduledTaskApplier(runner);

        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Uninstall_WhenTheTaskExists_DeletesItForced()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner);

        (await applier.IsInstalledAsync(CancellationToken.None)).ShouldBeTrue();
        (await applier.UninstallAsync(CancellationToken.None)).ShouldBeTrue();

        runner.Commands().ShouldBe(
        [
            "schtasks.exe /Query /TN squeezr-watchd",
            "schtasks.exe /Query /TN squeezr-watchd",
            "schtasks.exe /Delete /TN squeezr-watchd /F",
        ]);
    }

    [Fact]
    public async Task Uninstall_WhenDeleteFails_ReportsTheToolAnswer()
    {
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("/Delete")
            ? new AutostartProcessResult(1, string.Empty, "ERROR: The task is currently running.")
            : FakeAutostartProcessRunner.Ok);
        var applier = new WindowsScheduledTaskApplier(runner);

        var error = await Should.ThrowAsync<AutostartException>(() => applier.UninstallAsync(CancellationToken.None));

        error.Message.ShouldContain("failed to delete the scheduled task (exit 1)");
        error.Message.ShouldContain("The task is currently running.");
    }

    [Fact]
    public async Task Uninstall_WithACustomUnitName_AsksAboutThatNameOnly()
    {
        var runner = new FakeAutostartProcessRunner();
        var applier = new WindowsScheduledTaskApplier(runner, "watchd-lane");

        await applier.UninstallAsync(CancellationToken.None);

        runner.Commands().ShouldBe(["schtasks.exe /Query /TN watchd-lane", "schtasks.exe /Delete /TN watchd-lane /F"]);
        applier.ManagedTarget.ShouldBe("Task Scheduler task 'watchd-lane'");
    }
}
