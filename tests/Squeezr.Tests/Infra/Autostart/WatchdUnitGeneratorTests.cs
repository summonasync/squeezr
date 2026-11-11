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
    private const string Dotnet = "/usr/bin/dotnet";
    private const string Payload = "/opt/squeezr/squeezr.dll";
    private const string WindowsDotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private const string WindowsPayload = @"C:\Program Files\Squeezr\squeezr.dll";
    private const string WindowsPayloadWithQuote = @"C:\Program Files\sq""ueezr\squeezr.dll";
    private const string WindowsPayloadWithPercent = @"C:\Users\%USERNAME%\squeezr.dll";

    [Fact]
    public void Generate_SystemdUser_ReturnsUnitFileText()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary).ShouldBe(FileText("""
            # squeezr-watchd.service — managed by squeezr, do not edit
            [Unit]
            Description=squeezr watch daemon
            StartLimitIntervalSec=60
            StartLimitBurst=5

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
            StartLimitIntervalSec=60
            StartLimitBurst=5

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

    // The rate limit belongs to systemd's [Unit] section and to nothing else: launchd throttles on its
    // own terms and schtasks has no start limit at all, so these two lines must never leak into the
    // plist or the task command line, where they would be inert noise at best.
    [Fact]
    public void Generate_StartRateLimitIsCarriedOnlyByTheSystemdUnit()
    {
        var unit = WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary, Home);

        unit.ShouldContain("StartLimitIntervalSec=60");
        unit.ShouldContain("StartLimitBurst=5");
        WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary, Home)
            .ShouldNotContain("StartLimit");
        WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, WindowsBinary, WindowsHome)
            .ShouldNotContain("StartLimit");
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

    // The shape install writes when the CLI itself runs through the muxer: the program token is the
    // dotnet host, the assembly it loads is a separate token behind it, and the verb and the flag stay
    // the generator's own tail. The pair is what the init system execs — a unit naming the assembly
    // alone dies at exec with 126 while looking installed (review-110-03 BLOCKER-1).
    [Fact]
    public void Generate_SystemdUserWithTheMuxerPair_QuotesEveryTokenOfThePair()
    {
        var launch = WatchdUnitGenerator.LaunchPair(Dotnet, Payload);

        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, launch, Home).ShouldBe(FileText("""
            # squeezr-watchd.service — managed by squeezr, do not edit
            [Unit]
            Description=squeezr watch daemon
            StartLimitIntervalSec=60
            StartLimitBurst=5

            [Service]
            Type=simple
            Environment="SQUEEZR_HOME=/var/tmp/squeezr-home"
            ExecStart="/usr/bin/dotnet" "/opt/squeezr/squeezr.dll" watchd --autostart
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """));
    }

    [Fact]
    public void Generate_WindowsScheduledTaskWithTheMuxerPair_CarriesBothTokensInsideTheTaskCommand()
    {
        var launch = WatchdUnitGenerator.LaunchPair(WindowsDotnet, WindowsPayload);

        WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, launch).ShouldBe(ArgLine("""
            /Create /TN "squeezr-watchd" /SC ONLOGON /TR "\"C:\Program Files\dotnet\dotnet.exe\" \"C:\Program Files\Squeezr\squeezr.dll\" watchd --autostart"
            """));
        WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, launch, WindowsHome)
            .ShouldBe(ArgLine("""
            /Create /TN "squeezr-watchd" /SC ONLOGON /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\dotnet\dotnet.exe\" \"C:\Program Files\Squeezr\squeezr.dll\" watchd --autostart"
            """));
    }

    // launchd takes the argv as a list, so the pair is four tokens in the array and nothing about the
    // muxer needs quoting at all — the reason plist is the surface where the pair is cheapest to read.
    [Fact]
    public void Generate_LaunchAgentWithTheMuxerPair_ListsThePairAsProgramArguments()
    {
        var launch = WatchdUnitGenerator.LaunchPair(Dotnet, Payload);

        WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, launch).ShouldBe(FileText("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <!-- managed by squeezr, do not edit -->
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>dev.summonasync.squeezr-watchd</string>
                <key>ProgramArguments</key>
                <array>
                  <string>/usr/bin/dotnet</string>
                  <string>/opt/squeezr/squeezr.dll</string>
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

    // The regression stated as an equation rather than as frozen files: the apphost pair — a program
    // with nothing behind it — is the same answer the single-path call has always given, on every
    // surface. The snapshots above the pair tests are the byte-for-byte half of the same promise.
    [Theory]
    [InlineData(AutostartPlatform.SystemdUser)]
    [InlineData(AutostartPlatform.WindowsScheduledTask)]
    [InlineData(AutostartPlatform.LaunchAgent)]
    public void Generate_AppHostPair_MatchesTheSinglePathFormByteForByte(AutostartPlatform platform)
    {
        var single = WatchdUnitGenerator.Generate(platform, Binary, Home, "watchd-lane");
        var pair = WatchdUnitGenerator.Generate(
            platform, WatchdUnitGenerator.LaunchPair(Binary), Home, "watchd-lane");

        Encoding.UTF8.GetBytes(pair).ShouldBe(Encoding.UTF8.GetBytes(single));
    }

    // The verb and the flag belong to the generator: a caller that hands over the whole tail would
    // otherwise bake a unit exec'ing 'watchd --autostart watchd --autostart', which is the same class
    // of silent breakage the pair form exists to end.
    [Fact]
    public void Generate_PairCarryingTheWatchdTail_ThrowsInsteadOfDoublingIt()
    {
        var launch = new WatchdRunCommand(
            Binary, [WatchdUnitGenerator.WatchdVerb, WatchdUnitGenerator.AutostartFlag]);

        var error = Should.Throw<ArgumentException>(
            () => WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, launch));

        error.ParamName.ShouldBe("launch");
        error.Message.ShouldContain("watchd --autostart");
    }

    // Each token of the pair is escaped on its own — a payload carrying the characters systemd
    // expands never travels inside the program's quotes, where one more layer would corrupt it.
    [Fact]
    public void Generate_MuxerPayloadWithDollarAndPercent_EscapesItsOwnToken()
    {
        var launch = new WatchdRunCommand(Dotnet, ["/opt/sq%s/we$d/squeezr.dll"]);

        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, launch)
            .ShouldContain("""
                ExecStart="/usr/bin/dotnet" "/opt/sq%%s/we$$d/squeezr.dll" watchd --autostart
                """);
    }

    // A pair built without the factory still cannot smuggle a line break into line-oriented text.
    [Theory]
    [InlineData("/opt/squeezr\nsqueezr.dll")]
    [InlineData("/opt/squeezr\rsqueezr.dll")]
    public void Generate_PairPayloadWithLineBreak_Throws(string payload)
    {
        var launch = new WatchdRunCommand(Binary, [payload]);

        var error = Should.Throw<ArgumentException>(
            () => WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, launch));

        error.ParamName.ShouldBe("launch");
    }

    // Both tokens of the pair sit inside /TR, so cmd.exe re-parses both: neither the muxer nor its
    // payload may carry a quote or a percent, and the rule is the same one that already covers a lone
    // apphost.
    [Theory]
    [InlineData(WindowsDotnet, WindowsPayloadWithQuote, "launch")]
    [InlineData(WindowsDotnet, WindowsPayloadWithPercent, "launch")]
    [InlineData(WindowsPayloadWithQuote, WindowsPayload, "launch")]
    public void Generate_WindowsPairTokenTheCommandWouldReParse_Throws(
        string program,
        string payload,
        string expectedParameterName)
    {
        var launch = new WatchdRunCommand(program, [payload]);

        var error = Should.Throw<ArgumentException>(
            () => WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, launch));

        error.ParamName.ShouldBe(expectedParameterName);
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
        var action = () => WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, binaryPath: null!);

        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Generate_UnknownPlatform_ThrowsArgumentOutOfRange()
    {
        var action = () => WatchdUnitGenerator.Generate((AutostartPlatform)99, Binary);

        action.ShouldThrow<ArgumentOutOfRangeException>();
    }

    // The pair install hands the generator: the program the entry execs, plus the token it loads behind
    // it. The verb and the flag are not in the pair — the generator appends them to every surface.
    [Fact]
    public void LaunchPair_AppHostCarriesTheProgramAlone()
    {
        var launch = WatchdUnitGenerator.LaunchPair("/opt/my dir/squeezr");

        launch.Executable.ShouldBe("/opt/my dir/squeezr");
        launch.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void LaunchPair_MuxerCarriesTheAssemblyItLoads()
    {
        var launch = WatchdUnitGenerator.LaunchPair(Dotnet, Payload);

        launch.Executable.ShouldBe(Dotnet);
        launch.Arguments.ShouldBe([Payload]);
    }

    [Fact]
    public void LaunchPair_ValueWithShellMetacharacters_IsReturnedVerbatim()
    {
        const string binaryPath = "/opt/мой dir/we\\ird\"$n/%i/squeezr";

        WatchdUnitGenerator.LaunchPair(binaryPath).Executable.ShouldBe(binaryPath);
    }

    // The same non-interchangeability rule T1 pinned, now measured on both launch shapes: the argv pair
    // carries neither the cmd wrapper, nor the SQUEEZR_HOME assignment, nor the extra layer of escaping
    // the /TR token needs — and a muxed pair keeps both of its tokens inside that wrapper.
    [Fact]
    public void LaunchPair_IsNotInterchangeableWithTheSchtasksTaskCommand()
    {
        var appHost = WatchdUnitGenerator.LaunchPair(WindowsBinary);
        var muxed = WatchdUnitGenerator.LaunchPair(WindowsDotnet, WindowsPayload);
        var schtasks = WatchdUnitGenerator.Generate(
            AutostartPlatform.WindowsScheduledTask, muxed, WindowsHome);

        PlainArgv(appHost).ShouldBe(@"C:\Program Files\Squeezr\squeezr.exe watchd --autostart");
        PlainArgv(muxed).ShouldBe(
            @"C:\Program Files\dotnet\dotnet.exe C:\Program Files\Squeezr\squeezr.dll watchd --autostart");
        PlainArgv(muxed).ShouldNotContain("SQUEEZR_HOME");
        PlainArgv(muxed).ShouldNotContain("\\\"");
        schtasks.ShouldContain("""
            /TR "cmd /c set \"SQUEEZR_HOME=D:\squeezr home\" && \"C:\Program Files\dotnet\dotnet.exe\" \"C:\Program Files\Squeezr\squeezr.dll\" watchd --autostart"
            """);
    }

    [Theory]
    [InlineData("/opt/squeezr\nsqueezr")]
    [InlineData("/opt/squeezr\rhome")]
    [InlineData("")]
    [InlineData("   ")]
    public void LaunchPair_ValueWithLineBreakOrBlank_Throws(string binaryPath)
    {
        var error = Should.Throw<ArgumentException>(() => WatchdUnitGenerator.LaunchPair(binaryPath));

        error.ParamName.ShouldBe(nameof(binaryPath));
    }

    [Theory]
    [InlineData("/opt/squeezr\nsqueezr.dll")]
    [InlineData("/opt/squeezr\rsqueezr.dll")]
    [InlineData("")]
    [InlineData("   ")]
    public void LaunchPair_PayloadWithLineBreakOrBlank_Throws(string payloadPath)
    {
        var error = Should.Throw<ArgumentException>(
            () => WatchdUnitGenerator.LaunchPair(Dotnet, payloadPath));

        error.ParamName.ShouldBe(nameof(payloadPath));
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

    private static string PlainArgv(WatchdRunCommand launch)
    {
        string[] argv =
        [
            launch.Executable,
            ..launch.Arguments,
            WatchdUnitGenerator.WatchdVerb,
            WatchdUnitGenerator.AutostartFlag,
        ];
        return string.Join(' ', argv);
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
