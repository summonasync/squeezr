using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class WindowsScheduledTaskApplierTests
{
    private const string Binary = @"C:\Program Files\Squeezr\squeezr.exe";
    private const string Home = @"D:\squeezr home";
    private const string Schtasks = "schtasks.exe";

    // The answer a query with /XML gives for a task our own install created: the cmd wrapper the
    // generator builds to carry a non-default SQUEEZR_HOME, with the program it really starts sitting
    // behind the last quoted "&&". Hand-written from the task schema — this lane has no Windows host to
    // ask, so what is pinned here is our reader against the shape; the shape against a live Task
    // Scheduler is owner-gated and named in the task's obstacles.
    private const string TaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\squeezr-watchd</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger />
          </Triggers>
          <Settings>
            <RestartOnFailure>
              <Interval>PT1M</Interval>
              <Count>3</Count>
            </RestartOnFailure>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>cmd.exe</Command>
              <Arguments>/c set "SQUEEZR_HOME=D:\squeezr home" &amp;&amp; "C:\Program Files\Squeezr\squeezr.exe" watchd --autostart</Arguments>
            </Exec>
          </Actions>
          <RuntimeInfo>
            <LastTaskResult>0</LastTaskResult>
          </RuntimeInfo>
        </Task>
        """;

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

        (await applier.UninstallAsync(CancellationToken.None)).WasRemoved.ShouldBeFalse();

        runner.Commands().ShouldBe(["schtasks.exe /Query /TN squeezr-watchd"]);
    }

    [Fact]
    public async Task Probe_WhenSchtasksNamesAMissingTask_SaysAbsent()
    {
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("/Query")
            ? new AutostartProcessResult(1, string.Empty, "ERROR: The system cannot find the file specified.")
            : FakeAutostartProcessRunner.Ok);
        var applier = new WindowsScheduledTaskApplier(runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.No);
        probe.Probed.ShouldBeTrue();
        runner.Commands().ShouldBe(["schtasks.exe /Query /TN squeezr-watchd /XML"]);
    }

    // The MINOR-5 case: schtasks answers exit 1 for a missing task and for most of its other refusals,
    // so "access denied" must come back as a question that was not answered. Reporting it as absent is
    // how a live task gets a clean bill of health from a diagnostic that never got to look.
    [Fact]
    public async Task Probe_WhenSchtasksRefusesToAnswer_SaysItCouldNotAskAndNotAbsent()
    {
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(1, string.Empty, "ERROR: Access is denied."));
        var applier = new WindowsScheduledTaskApplier(runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.Unknown);
        probe.Probed.ShouldBeTrue();
        probe.Notes.ShouldHaveSingleItem().ShouldContain("Access is denied.");
    }

    [Fact]
    public async Task Probe_WhenSchtasksCannotBeStarted_SaysTheWholeProbeIsUnsupported()
    {
        var applier = new WindowsScheduledTaskApplier(new FakeAutostartProcessRunner(
            (_, _) => throw new AutostartException("Could not start 'schtasks.exe'.")));

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Probed.ShouldBeFalse();
        probe.Presence.ShouldBe(WatchdUnitAnswer.Unknown);
        probe.Notes.ShouldHaveSingleItem().ShouldContain("schtasks could not be asked about the task");
    }

    [Fact]
    public async Task Probe_WhenTheTaskAnswersItsXml_ReadsTheFlagsTheTaskCarries()
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(0, TaskXml, string.Empty));
        var applier = new WindowsScheduledTaskApplier(runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Enabled.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Failed.ShouldBe(WatchdUnitAnswer.No);
        probe.UnitText.ShouldBe(TaskXml);
    }

    [Fact]
    public async Task Probe_WhenTheTaskIsDisabled_SaysItIsPresentButNotEnabled()
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>"),
            string.Empty));
        var applier = new WindowsScheduledTaskApplier(runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Presence.ShouldBe(WatchdUnitAnswer.Yes);
        probe.Enabled.ShouldBe(WatchdUnitAnswer.No);
    }

    // An XML answer without the Settings flag is the schema default, not an answer this reader may
    // invent: it comes back as "could not ask" and leaves the verdict to the lines that do have one.
    [Fact]
    public async Task Probe_WhenTheXmlNamesNoEnabledFlag_SaysItCouldNotAsk()
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml.Replace("<Enabled>true</Enabled>", string.Empty),
            string.Empty));
        var applier = new WindowsScheduledTaskApplier(runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Enabled.ShouldBe(WatchdUnitAnswer.Unknown);
        probe.Failed.ShouldBe(WatchdUnitAnswer.No);
    }

    // A task that has never run has no outcome to report; that is a platform with nothing to say about
    // failure, not a failed task and not an answered healthy one either.
    [Fact]
    public async Task Probe_WhenTheTaskNeverRan_SaysItHasNoFailureSignal()
    {
        var runner = new FakeAutostartProcessRunner((_, _) => new AutostartProcessResult(
            0,
            TaskXml.Replace("<LastTaskResult>0</LastTaskResult>", "<LastTaskResult>267009</LastTaskResult>"),
            string.Empty));
        var applier = new WindowsScheduledTaskApplier(runner);

        var probe = await applier.ProbeAsync(CancellationToken.None);

        probe.Failed.ShouldBe(WatchdUnitAnswer.Unsupported);
        probe.Enabled.ShouldBe(WatchdUnitAnswer.Yes);
    }

    [Fact]
    public async Task Uninstall_WhenTheTaskExists_DeletesItForced()
    {
        var runner = new FakeAutostartProcessRunner((_, arguments) => arguments.Contains("/XML")
            ? new AutostartProcessResult(0, TaskXml, string.Empty)
            : FakeAutostartProcessRunner.Ok);
        var applier = new WindowsScheduledTaskApplier(runner);

        (await applier.ProbeAsync(CancellationToken.None)).Presence.ShouldBe(WatchdUnitAnswer.Yes);
        (await applier.UninstallAsync(CancellationToken.None)).WasRemoved.ShouldBeTrue();

        runner.Commands().ShouldBe(
        [
            "schtasks.exe /Query /TN squeezr-watchd /XML",
            "schtasks.exe /Query /TN squeezr-watchd",
            "schtasks.exe /Delete /TN squeezr-watchd /F",
        ]);
    }

    // The write-side half of MINOR-5: a store that will not say whether the task is there must not end
    // up with uninstall claiming the entry is gone — nothing was deleted and the operator has to read
    // that, not a confident "nothing was removed".
    [Fact]
    public async Task Uninstall_WhenSchtasksWillNotSay_KeepsTheTaskAndCarriesTheRefusal()
    {
        var runner = new FakeAutostartProcessRunner((_, _) =>
            new AutostartProcessResult(1, string.Empty, "ERROR: Access is denied."));
        var applier = new WindowsScheduledTaskApplier(runner);

        var removal = await applier.UninstallAsync(CancellationToken.None);

        removal.WasRemoved.ShouldBeFalse();
        removal.Warnings.ShouldHaveSingleItem().ShouldContain("Access is denied.");
        removal.Warnings.Single().ShouldContain("nothing was deleted");
        runner.Commands().ShouldBe(["schtasks.exe /Query /TN squeezr-watchd"]);
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
