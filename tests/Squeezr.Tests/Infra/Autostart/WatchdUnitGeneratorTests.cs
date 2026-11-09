using System.Text;
using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class WatchdUnitGeneratorTests
{
    private const string Binary = "/opt/squeezr/squeezr";
    private const string Home = "/var/tmp/squeezr-home";
    private const string WindowsBinary = @"C:\Program Files\Squeezr\squeezr.exe";

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
                <string>squeezr-watchd</string>
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
                <string>squeezr-watchd</string>
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
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.WindowsScheduledTask)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_UnitNameOverride_IsNamedOnEveryPlatform(AutostartPlatform platform)
    {
        var text = WatchdUnitGenerator.Generate(platform, Binary, unitName: "watchd-lane");

        text.ShouldContain("watchd-lane");
        text.ShouldNotContain(WatchdUnitGenerator.DefaultUnitName);
    }

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.WindowsScheduledTask)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_BlankUnitName_FallsBackToDefaultName(AutostartPlatform platform)
    {
        var text = WatchdUnitGenerator.Generate(platform, Binary, unitName: "  ");

        text.ShouldContain(WatchdUnitGenerator.DefaultUnitName);
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
    public void GeneratorFiles_DoNotProbeTheHostOs()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Squeezr.Core", "Infra", "Autostart");
        foreach (var file in new[] { "AutostartPlatform.cs", "WatchdUnitGenerator.cs" })
        {
            var source = File.ReadAllText(Path.Combine(dir, file));
            foreach (var probe in new[] { "RuntimeInformation", "OSVersion", "OperatingSystem." })
            {
                source.ShouldNotContain(probe);
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
