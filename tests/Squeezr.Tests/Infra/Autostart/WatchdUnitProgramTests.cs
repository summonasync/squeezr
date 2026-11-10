using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class WatchdUnitProgramTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string WindowsBinary = @"C:\Program Files\Squeezr\squeezr.exe";
    private const string WindowsHome = @"D:\squeezr home";

    // The task-XML shape for a task install created: schtasks stores the /TR payload as the action, and
    // cmd carries the env assignment in front of the program. Hand-written from the schema, and tied to
    // the generator's own bytes by the test at the bottom of this file — this lane has no Windows host
    // to ask, so the shape against a live Task Scheduler stays owner-gated.
    private const string WrappedCmdPayload =
        "/c set \"SQUEEZR_HOME=D:\\squeezr home\" && \"C:\\Program Files\\Squeezr\\squeezr.exe\" watchd --autostart";

    private const string WrappedWholeLine =
        "cmd /c set \"SQUEEZR_HOME=D:\\squeezr home\" && \"C:\\Program Files\\Squeezr\\squeezr.exe\" watchd --autostart";

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void GenerateThenRead_NamesTheProgramInstallBakedIn(AutostartPlatform platform)
    {
        var program = TestProgram.Executable("squeezr");

        var command = WatchdUnitProgram.Read(
            platform,
            WatchdUnitGenerator.Generate(platform, program, Home));

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(program);
        command.Arguments.ShouldBe(["watchd", "--autostart"]);
    }

    [Fact]
    public void Read_OnWindows_UnwrapsTheCmdWrapperAndNamesTheProgramBehindIt()
    {
        var carried = $"""
            <Task version="1.2">
              <Actions><Exec><Command>cmd.exe</Command><Arguments>{Xml(WrappedCmdPayload)}</Arguments></Exec></Actions>
            </Task>
            """;

        var command = WatchdUnitProgram.Read(AutostartPlatform.WindowsScheduledTask, carried);

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(WindowsBinary);
        command.Arguments.ShouldBe(["watchd", "--autostart"]);
    }

    // Some exports put the whole command in the <Command> element with no arguments beside it; the
    // program behind the wrapper is the same answer either way.
    [Fact]
    public void Read_OnWindows_ReadsTheWholeTrStringCarriedAsOneCommandElement()
    {
        var carried = $"""
            <Task version="1.2"><Actions><Exec><Command>{Xml(WrappedWholeLine)}</Command></Exec></Actions></Task>
            """;

        var command = WatchdUnitProgram.Read(AutostartPlatform.WindowsScheduledTask, carried);

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(WindowsBinary);
    }

    [Fact]
    public void Read_OnWindows_WithoutTheHomeWrapper_KeepsThePlainCommand()
    {
        var carried = $$"""
            <Task version="1.2"><Actions><Exec>
              <Command>"{{WindowsBinary}}"</Command>
              <Arguments>watchd --autostart</Arguments>
            </Exec></Actions></Task>
            """;

        var command = WatchdUnitProgram.Read(AutostartPlatform.WindowsScheduledTask, carried);

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(WindowsBinary);
        command.Arguments.ShouldBe(["watchd", "--autostart"]);
    }

    [Fact]
    public void Read_OnWindows_WhenTheXmlCarriesNoExec_NamesNoProgram() =>
        WatchdUnitProgram.Read(
            AutostartPlatform.WindowsScheduledTask,
            "<Task><Settings><Enabled>true</Enabled></Settings></Task>").ShouldBeNull();

    // The generator escapes a different set on every surface ($$ and %% in ExecStart, cmd quoting inside
    // /TR, XML entities in the plist), so the reader is only correct if a path that survives its own
    // escaping comes back byte for byte.
    [Fact]
    public void Read_OnSystemd_UnescapesTheTokensTheGeneratorEscaped()
    {
        var program = "/opt/sq/we ird/$HOME/%tag/\"quoted\"/back\\slash/squeezr";

        var command = WatchdUnitProgram.Read(
            AutostartPlatform.SystemdUser,
            WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, program));

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(program);
    }

    [Fact]
    public void Read_OnLaunchAgent_UnescapesTheEntitiesThePlistCarries()
    {
        var program = "/opt/sq/a & b/<not-a-tag>/squeezr";

        var command = WatchdUnitProgram.Read(
            AutostartPlatform.LaunchAgent,
            WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, program));

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(program);
    }

    // systemd.exec(5) quotes a run with either sign, and a hand-edited unit carrying the single-quoted
    // form names one path with a space in it — the same answer the double-quoted form gives. Inside
    // single quotes nothing is escaped, which is why the sign is read as a run and not as a literal.
    [Theory]
    [InlineData("""ExecStart='/opt/my dir/squeezr' watchd --autostart""")]
    [InlineData("""ExecStart="/opt/my dir/squeezr" watchd --autostart""")]
    public void Read_OnSystemd_KeepsAQuotedRunWithASpaceInsideOneToken(string statement)
    {
        var command = WatchdUnitProgram.Read(
            AutostartPlatform.SystemdUser, $"[Service]\n{statement}\n");

        command.ShouldNotBeNull();
        command.Executable.ShouldBe("/opt/my dir/squeezr");
        command.Arguments.ShouldBe(["watchd", "--autostart"]);
    }

    [Fact]
    public void Read_OnSystemd_TakesTheFirstTokenAndKeepsTheRestAsArguments()
    {
        var text = """
            [Unit]
            Description=squeezr watch daemon

            [Service]
            ExecStart=/usr/bin/dotnet /opt/squeezr/squeezr.dll watchd --autostart
            Restart=on-failure
            """;

        var command = WatchdUnitProgram.Read(AutostartPlatform.SystemdUser, text);

        command.ShouldNotBeNull();
        command.Executable.ShouldBe("/usr/bin/dotnet");
        command.Arguments.ShouldBe(["/opt/squeezr/squeezr.dll", "watchd", "--autostart"]);
    }

    // A unit edited by hand can carry the systemd modifier prefixes; the manager strips them before it
    // execs, so the program the entry names is the path behind them, not the prefix.
    [Theory]
    [InlineData("ExecStart=@/opt/squeezr/squeezr watchd --autostart")]
    [InlineData("ExecStart=-/opt/squeezr/squeezr watchd --autostart")]
    public void Read_OnSystemd_StripsTheModifierPrefixAHandEditedUnitCarries(string statement)
    {
        var command = WatchdUnitProgram.Read(AutostartPlatform.SystemdUser, $"[Service]\n{statement}\n");

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(Binary);
    }

    [Fact]
    public void Read_OnSystemd_WhenTheUnitCarriesNoExecStart_NamesNoProgram() =>
        WatchdUnitProgram.Read(
            AutostartPlatform.SystemdUser,
            "[Service]\nType=simple\nExecStop=/bin/true\n").ShouldBeNull();

    [Fact]
    public void Read_OnLaunchAgent_FallsBackToTheLegacyProgramKey()
    {
        var text = """
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>dev.summonasync.squeezr-watchd</string>
                <key>Program</key>
                <string>/opt/squeezr/squeezr</string>
              </dict>
            </plist>
            """;

        var command = WatchdUnitProgram.Read(AutostartPlatform.LaunchAgent, text);

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(Binary);
        command.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void Read_OnLaunchAgent_WhenTheTextIsNotAPlist_NamesNoProgram() =>
        WatchdUnitProgram.Read(AutostartPlatform.LaunchAgent, "ExecStart=/opt/squeezr/squeezr").ShouldBeNull();

    // The DTD posture of the reader, pinned by its two halves. A plist carrying an external entity is
    // read as nothing: the DTD is skipped, the entity stays undefined, the parse fails and no file off
    // the disk is opened as a side effect of a diagnostic. The other half is why the posture is Ignore
    // and not Prohibit — every plist install generates carries the public DOCTYPE, and a reader that
    // refused one would refuse its own entries.
    [Fact]
    public void Read_OnLaunchAgent_DtdCarriesNoAnswerAndStillStandsForOurOwnPlists()
    {
        var xxe = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd" [
              <!ENTITY xxe SYSTEM "file:///etc/passwd">
            ]>
            <plist version="1.0">
              <dict>
                <key>ProgramArguments</key>
                <array><string>&xxe;</string></array>
              </dict>
            </plist>
            """;

        WatchdUnitProgram.Read(AutostartPlatform.LaunchAgent, xxe).ShouldBeNull();

        var command = WatchdUnitProgram.Read(
            AutostartPlatform.LaunchAgent,
            WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary));

        command.ShouldNotBeNull();
        command.Executable.ShouldBe(Binary);
    }

    [Fact]
    public void Read_WithNoTextAtAll_NamesNoProgram()
    {
        WatchdUnitProgram.Read(AutostartPlatform.SystemdUser, null).ShouldBeNull();
        WatchdUnitProgram.Read(AutostartPlatform.LaunchAgent, "   ").ShouldBeNull();
    }

    // Spec decision 3, read back: after an upgrade deletes the directory the unit was installed from,
    // the entry still names that exact path. This is what 'watchd installed' has to report instead of
    // restating what install would write today.
    [Fact]
    public void Read_OnSystemd_ReturnsTheDeletedPathTheUnitStillNames()
    {
        var gone = Path.Combine(TestDirs.NewTempDir(), "2.0.0", "squeezr");
        var text = WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, gone);

        WatchdUnitProgram.Read(AutostartPlatform.SystemdUser, text)!.Executable.ShouldBe(gone);
        File.Exists(gone).ShouldBeFalse();
    }

    // The tie between the reader's fixtures and the writer's bytes: the cmd payload the tests above
    // hand over is the same string install puts inside /TR, quoted the way the generator quotes it.
    [Fact]
    public void GeneratorAndReader_AgreeOnTheCmdPayloadTheWindowsBranchWrites()
    {
        var line = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, WindowsBinary, WindowsHome);

        line.ShouldContain("""
            /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """);
    }

    private static string Xml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
