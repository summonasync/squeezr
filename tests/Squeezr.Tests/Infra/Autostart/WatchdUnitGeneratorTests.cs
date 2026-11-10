using System.Text;
using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class WatchdUnitGeneratorTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string WindowsBinary = @"C:\Program Files\Squeezr\squeezr.exe";
    private const string WindowsHome = @"D:\squeezr home";
    private const string WindowsBinaryWithQuote = @"C:\Program Files\sq""ueezr\squeezr.exe";
    private const string WindowsHomeWithQuote = @"D:\evil"" & calc & ""x";
    private const string WindowsUnitNameWithQuote = @"watchd"" & calc & ""lane";
    private const string WindowsBinaryWithPercent = @"C:\Users\%USERNAME%\squeezr.exe";
    private const string WindowsHomeWithPercent = @"D:\%PUBLIC% home";

    [Fact]
    public void Generate_SystemdUser_ReturnsUnitFileText()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary).ShouldBe(FileText("""
            # squeezr-watchd.service — managed by squeezr, do not edit
            [Unit]
            Description=squeezr watch daemon

            [Service]
            Type=simple
            ExecStart="/opt/squeezr/squeezr" watchd --autostart
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """));
    }

    [Fact]
    public void Generate_SystemdUserWithSqueezrHome_ReturnsUnitFileTextWithEnvironment()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, Home).ShouldBe(FileText("""
            # squeezr-watchd.service — managed by squeezr, do not edit
            [Unit]
            Description=squeezr watch daemon

            [Service]
            Type=simple
            Environment="SQUEEZR_HOME=/var/tmp/squeezr-home"
            ExecStart="/opt/squeezr/squeezr" watchd --autostart
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """));
    }

    [Fact]
    public void Generate_WindowsScheduledTask_ReturnsSchtasksArguments()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, WindowsBinary).ShouldBe(ArgLine("""
            /Create /TN "squeezr-watchd" /SC ONLOGON /TR "\"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """));
    }

    [Fact]
    public void Generate_WindowsScheduledTaskWithSqueezrHome_WrapsTaskCommandInCmdSet()
    {
        var text = WatchdUnitGenerator.Generate(
            AutostartPlatform.WindowsScheduledTask, WindowsBinary, @"D:\squeezr home");

        text.ShouldBe(ArgLine("""
            /Create /TN "squeezr-watchd" /SC ONLOGON /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """));
    }

    [Fact]
    public void Generate_LaunchAgent_ReturnsPlistText()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary).ShouldBe(FileText("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <!-- managed by squeezr, do not edit -->
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>dev.summonasync.squeezr-watchd</string>
                <key>ProgramArguments</key>
                <array>
                  <string>/opt/squeezr/squeezr</string>
                  <string>watchd</string>
                  <string>--autostart</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                  <key>SuccessfulExit</key>
                  <false/>
                </dict>
              </dict>
            </plist>
            """));
    }

    [Fact]
    public void Generate_LaunchAgentWithSqueezrHome_ReturnsPlistWithEnvironmentVariables()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary, Home).ShouldBe(FileText("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <!-- managed by squeezr, do not edit -->
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>dev.summonasync.squeezr-watchd</string>
                <key>ProgramArguments</key>
                <array>
                  <string>/opt/squeezr/squeezr</string>
                  <string>watchd</string>
                  <string>--autostart</string>
                </array>
                <key>EnvironmentVariables</key>
                <dict>
                  <key>SQUEEZR_HOME</key>
                  <string>/var/tmp/squeezr-home</string>
                </dict>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                  <key>SuccessfulExit</key>
                  <false/>
                </dict>
              </dict>
            </plist>
            """));
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.WindowsScheduledTask)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_SameInputsTwice_ReturnsByteIdenticalText(AutostartPlatform platform)
    {
        var first = WatchdUnitGenerator.Generate(platform, "/opt/my dir/squeezr", Home, "watchd-lane");
        var second = WatchdUnitGenerator.Generate(platform, "/opt/my dir/squeezr", Home, "watchd-lane");

        Encoding.UTF8.GetBytes(first).ShouldBe(Encoding.UTF8.GetBytes(second));
        first.ShouldNotContain("\r");
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.WindowsScheduledTask)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_WithoutSqueezrHome_CarriesNoEnvironment(AutostartPlatform platform)
    {
        var text = WatchdUnitGenerator.Generate(platform, Binary);

        text.ShouldNotContain("SQUEEZR_HOME");
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.WindowsScheduledTask)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_BlankSqueezrHome_CarriesNoEnvironment(AutostartPlatform platform)
    {
        var text = WatchdUnitGenerator.Generate(platform, Binary, "   ");

        text.ShouldNotContain("SQUEEZR_HOME");
    }

    [Fact]
    public void Generate_SystemdUserPathWithSpaceBackslashQuoteAndCyrillic_EscapesExecStart()
    {
        var text = WatchdUnitGenerator.Generate(
            AutostartPlatform.SystemdUser, "/opt/мой dir/we\\ird\"$n/%i/squeezr");

        text.ShouldContain("""ExecStart="/opt/мой dir/we\\ird\"$$n/%%i/squeezr" watchd --autostart""");
    }

    [Theory]
    [InlineData("/var/tmp/my$d$home", "Environment=\"SQUEEZR_HOME=/var/tmp/my$d$home\"")]
    [InlineData("/var/tmp/%h/squeezr", "Environment=\"SQUEEZR_HOME=/var/tmp/%%h/squeezr\"")]
    [InlineData("/var/tmp/a$b%c", "Environment=\"SQUEEZR_HOME=/var/tmp/a$b%%c\"")]
    public void Generate_SystemdUserHomeWithDollarOrPercent_EscapesOnlySpecifiersInEnvironment(
        string squeezrHome,
        string expectedLine)
    {
        var text = WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, squeezrHome);

        text.ShouldContain(expectedLine);
    }

    [Fact]
    public void Generate_SystemdUserDollarAndPercentInBothInputs_EscapesEachSurfaceItsOwnWay()
    {
        var text = WatchdUnitGenerator.Generate(
            AutostartPlatform.SystemdUser, "/opt/sq%s/we$d/squeezr", "/var/tmp/my$dhome/%i");

        text.ShouldContain("""ExecStart="/opt/sq%%s/we$$d/squeezr" watchd --autostart""");
        text.ShouldContain("""
            Environment="SQUEEZR_HOME=/var/tmp/my$dhome/%%i"
            """);
    }

    [Fact]
    public void Generate_HomeWithDoubleQuoteOutsideWindows_KeepsPerSurfaceEscaping()
    {
        var systemd = WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, @"/var/tmp/my""home");
        var plist = WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary, @"/var/tmp/my""home");

        systemd.ShouldContain("""
            Environment="SQUEEZR_HOME=/var/tmp/my\"home"
            """);
        plist.ShouldContain("""<string>/var/tmp/my"home</string>""");
    }

    [Fact]
    public void Generate_LaunchAgentPathWithSpaceBackslashAndXmlMarkup_EscapesPlistString()
    {
        var text = WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, @"/opt/мой a<b>&c\d/squeezr");

        text.ShouldContain("""<string>/opt/мой a&lt;b&gt;&amp;c\d/squeezr</string>""");
    }

    [Fact]
    public void Generate_WindowsBinaryPathEndingWithSeparator_DoublesBackslashesBeforeQuote()
    {
        var text = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, @"C:\squeezr\app\");

        text.ShouldBe(ArgLine("""
            /Create /TN "squeezr-watchd" /SC ONLOGON /TR "\"C:\squeezr\app\\\" watchd --autostart"
            """));
    }

    [Theory]
    [InlineData(WindowsBinaryWithQuote, WindowsHome, null, "binaryPath")]
    [InlineData(WindowsBinary, WindowsHomeWithQuote, null, "squeezrHome")]
    [InlineData(WindowsBinary, WindowsHome, WindowsUnitNameWithQuote, "unitName")]
    public void Generate_WindowsValueWithDoubleQuote_Throws(
        string binaryPath,
        string squeezrHome,
        string? unitName,
        string expectedParameterName)
    {
        var error = Should.Throw<ArgumentException>(() => WatchdUnitGenerator.Generate(
            AutostartPlatform.WindowsScheduledTask, binaryPath, squeezrHome, unitName));

        error.ParamName.ShouldBe(expectedParameterName);
    }

    [Fact]
    public void Generate_WindowsHomeWithAmpersand_StaysLiteralInsideBalancedQuotes()
    {
        var text = WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, WindowsBinary, @"D:\a&b");

        text.ShouldBe(ArgLine("""
            /Create /TN "squeezr-watchd" /SC ONLOGON /TR "cmd /c set \"SQUEEZR_HOME=D:\a&b\" && \"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """));
    }

    [Theory]
    [InlineData(WindowsBinaryWithPercent, WindowsHome, null, "binaryPath")]
    [InlineData(WindowsBinary, WindowsHomeWithPercent, null, "squeezrHome")]
    [InlineData(WindowsBinary, "%", null, "squeezrHome")]
    public void Generate_WindowsValueInsideTaskCommandWithPercent_Throws(
        string binaryPath,
        string squeezrHome,
        string? unitName,
        string expectedParameterName)
    {
        var error = Should.Throw<ArgumentException>(() => WatchdUnitGenerator.Generate(
            AutostartPlatform.WindowsScheduledTask, binaryPath, squeezrHome, unitName));

        error.ParamName.ShouldBe(expectedParameterName);
        error.Message.ShouldContain("percent sign");
    }

    // /TN never reaches cmd: the task name is an argument for schtasks, so the percent rule stops at
    // the two values that go inside /TR and a name that carries one still generates.
    [Fact]
    public void Generate_WindowsUnitNameWithPercent_GeneratesAndCarriesItIntoTheTaskName()
    {
        var text = WatchdUnitGenerator.Generate(
            AutostartPlatform.WindowsScheduledTask, WindowsBinary, WindowsHome, @"watchd%lane");

        text.ShouldBe(ArgLine("""
            /Create /TN "watchd%lane" /SC ONLOGON /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """));
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_PercentOutsideTheWindowsBranch_IsEscapedNotRefused(AutostartPlatform platform)
    {
        var text = WatchdUnitGenerator.Generate(
            platform, "/opt/%USERNAME%/squeezr", "/var/tmp/%PUBLIC% home");

        text.ShouldContain("%");
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser, WatchdUnitGenerator.DefaultUnitName)]
    [InlineData(AutostartPlatform.WindowsScheduledTask, WatchdUnitGenerator.DefaultUnitName)]
    [InlineData(AutostartPlatform.LaunchAgent, WatchdUnitGenerator.LaunchAgentUnitName)]
    public void Generate_UnitNameOverride_IsNamedOnEveryPlatform(
        AutostartPlatform platform,
        string platformDefault)
    {
        var text = WatchdUnitGenerator.Generate(platform, Binary, unitName: "watchd-lane");

        text.ShouldContain("watchd-lane");
        text.ShouldNotContain(platformDefault);
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser, WatchdUnitGenerator.DefaultUnitName)]
    [InlineData(AutostartPlatform.WindowsScheduledTask, WatchdUnitGenerator.DefaultUnitName)]
    [InlineData(AutostartPlatform.LaunchAgent, WatchdUnitGenerator.LaunchAgentUnitName)]
    public void Generate_BlankUnitName_FallsBackToThePlatformDefault(
        AutostartPlatform platform,
        string platformDefault)
    {
        var text = WatchdUnitGenerator.Generate(platform, Binary, unitName: "  ");

        text.ShouldContain(platformDefault);
    }

    [Theory]
    [InlineData("/opt/squeezr\nsqueezr", null, null)]
    [InlineData("/opt/squeezr\rhome", null, null)]
    [InlineData("/opt/squeezr", "/var/tmp\nhome", null)]
    [InlineData("/opt/squeezr", "/var/tmp\rhome", null)]
    [InlineData("/opt/squeezr", null, "watchd\nlane")]
    public void Generate_ValueWithLineBreak_Throws(string binaryPath, string? squeezrHome, string? unitName)
    {
        var action = () => WatchdUnitGenerator.Generate(
            AutostartPlatform.SystemdUser, binaryPath, squeezrHome, unitName);

        action.ShouldThrow<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_BlankBinaryPath_Throws(string binaryPath)
    {
        var action = () => WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, binaryPath);

        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Generate_NullBinaryPath_Throws()
    {
        var action = () => WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, null!);

        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Generate_UnknownPlatform_ThrowsArgumentOutOfRange()
    {
        var action = () => WatchdUnitGenerator.Generate((AutostartPlatform)99, Binary);

        action.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RunCommand_ReturnsExecutableAndWatchdArguments()
    {
        var command = WatchdUnitGenerator.RunCommand("/opt/my dir/squeezr");

        command.Executable.ShouldBe("/opt/my dir/squeezr");
        command.Arguments.ShouldBe([WatchdUnitGenerator.WatchdVerb, WatchdUnitGenerator.AutostartFlag]);
    }

    [Fact]
    public void RunCommand_ValueWithShellMetacharacters_IsReturnedVerbatim()
    {
        const string binaryPath = "/opt/мой dir/we\\ird\"$n/%i/squeezr";

        WatchdUnitGenerator.RunCommand(binaryPath).Executable.ShouldBe(binaryPath);
    }

    [Fact]
    public void RunCommand_IsNotInterchangeableWithTheSchtasksTaskCommand()
    {
        var command = WatchdUnitGenerator.RunCommand(WindowsBinary);
        var schtasks = WatchdUnitGenerator.Generate(
            AutostartPlatform.WindowsScheduledTask, WindowsBinary, WindowsHome);

        var plain = $"{command.Executable} {string.Join(' ', command.Arguments)}";

        plain.ShouldBe(@"C:\Program Files\Squeezr\squeezr.exe watchd --autostart");
        plain.ShouldNotContain("SQUEEZR_HOME");
        plain.ShouldNotContain("\\\"");
        schtasks.ShouldContain("""
            /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\Squeezr\squeezr.exe\" watchd --autostart"
            """);
    }

    [Theory]
    [InlineData("/opt/squeezr\nsqueezr")]
    [InlineData("/opt/squeezr\rhome")]
    [InlineData("")]
    [InlineData("   ")]
    public void RunCommand_ValueWithLineBreakOrBlank_Throws(string binaryPath)
    {
        var error = Should.Throw<ArgumentException>(() => WatchdUnitGenerator.RunCommand(binaryPath));

        error.ParamName.ShouldBe(nameof(binaryPath));
    }

    [Fact]
    public void AutostartSources_DoNotProbeTheHostOs()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Squeezr.Core", "Infra", "Autostart");
        var sources = Directory.EnumerateFiles(dir, "*.cs").ToArray();

        sources.ShouldContain(source => Path.GetFileName(source) == "WatchdUnitGenerator.cs");
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var probe in new[] { "RuntimeInformation", "OSVersion", "OperatingSystem." })
            {
                text.ShouldNotContain(probe);
            }
        }
    }

    private static string FileText(string block) => block.Replace("\r\n", "\n") + "\n";

    private static string ArgLine(string block) => block.Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
