using System.Text;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Infra.Autostart;

public static class WatchdUnitGenerator
{
    public const string DefaultUnitName = "squeezr-watchd";

    // launchd wants its labels reverse-DNS: the domain is what keeps one user's agents from colliding
    // with another vendor's, and the plist file name has to match the label it carries.
    public const string LaunchAgentUnitName = "dev.summonasync.squeezr-watchd";

    public const string WatchdVerb = "watchd";
    public const string AutostartFlag = "--autostart";
    public const string ManagedByMarker = "managed by squeezr, do not edit";

    // The start-limit window the systemd unit declares and how many starts may fit inside it. The
    // install warning quotes the window back at the operator, so the number has one owner instead of
    // two that can drift apart.
    public const int StartLimitWindowSeconds = 60;
    public const int StartLimitBurst = 5;

    private const string UnitDescription = "squeezr watch daemon";
    private const string PlistDoctype =
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">";

    public static string Generate(
        AutostartPlatform platform,
        string binaryPath,
        string? squeezrHome = null,
        string? unitName = null)
    {
        RequireText(binaryPath, nameof(binaryPath));
        RejectLineBreaks(binaryPath, nameof(binaryPath));
        return Generate(platform, new WatchdRunCommand(binaryPath, []), squeezrHome, unitName, nameof(binaryPath));
    }

    public static string Generate(
        AutostartPlatform platform,
        WatchdRunCommand launch,
        string? squeezrHome = null,
        string? unitName = null) =>
        Generate(platform, launch, squeezrHome, unitName, nameof(launch));

    private static string Generate(
        AutostartPlatform platform,
        WatchdRunCommand launch,
        string? squeezrHome,
        string? unitName,
        string launchParameterName)
    {
        ArgumentNullException.ThrowIfNull(launch);
        RejectLineBreaks(squeezrHome, nameof(squeezrHome));
        RejectLineBreaks(unitName, nameof(unitName));

        var tokens = LaunchTokens(launch, launchParameterName);
        var name = string.IsNullOrWhiteSpace(unitName) ? DefaultUnitNameFor(platform) : unitName;
        var home = string.IsNullOrWhiteSpace(squeezrHome) ? null : squeezrHome;

        return platform switch
        {
            AutostartPlatform.SystemdUser => SystemdUnit(name, tokens, home),
            AutostartPlatform.WindowsScheduledTask => SchtasksArguments(name, tokens, home, launchParameterName),
            AutostartPlatform.LaunchAgent => LaunchAgentPlist(name, tokens, home),
            _ => throw new ArgumentOutOfRangeException(
                nameof(platform), platform, $"Unknown autostart platform {(int)platform}."),
        };
    }

    public static WatchdRunCommand LaunchPair(string binaryPath, string? payloadPath = null)
    {
        RequireText(binaryPath, nameof(binaryPath));
        RejectLineBreaks(binaryPath, nameof(binaryPath));
        if (payloadPath is null)
        {
            return new WatchdRunCommand(binaryPath, []);
        }

        RequireText(payloadPath, nameof(payloadPath));
        RejectLineBreaks(payloadPath, nameof(payloadPath));
        return new WatchdRunCommand(binaryPath, [payloadPath]);
    }

    private static string DefaultUnitNameFor(AutostartPlatform platform) =>
        platform == AutostartPlatform.LaunchAgent ? LaunchAgentUnitName : DefaultUnitName;

    private static string SystemdUnit(string unitName, IReadOnlyList<string> launchTokens, string? squeezrHome)
    {
        var exec = string.Join(' ', launchTokens.Select(token => SystemdQuote(token, escapeDollarSign: true)));
        var lines = new List<string>
        {
            $"# {unitName}.service — {ManagedByMarker}",
            "[Unit]",
            $"Description={UnitDescription}",
            // systemd.unit(5) defaults the window to 10s, and a RestartSec=5 flap fits two starts in it —
            // the limit never trips and the unit flaps forever instead of coming to rest in failed. Named
            // explicitly so the start-limit step the install warning promises is a state that happens.
            $"StartLimitIntervalSec={StartLimitWindowSeconds}",
            $"StartLimitBurst={StartLimitBurst}",
            string.Empty,
            "[Service]",
            "Type=simple",
        };
        if (squeezrHome is not null)
        {
            var assignment = $"{SqueezrHome.HomeEnvironmentVariable}={squeezrHome}";
            lines.Add($"Environment={SystemdQuote(assignment, escapeDollarSign: false)}");
        }

        lines.AddRange(
        [
            $"ExecStart={exec} {WatchdVerb} {AutostartFlag}",
            "Restart=on-failure",
            "RestartSec=5",
            string.Empty,
            "[Install]",
            "WantedBy=default.target",
        ]);
        return FileText(lines);
    }

    private static string SchtasksArguments(
        string unitName,
        IReadOnlyList<string> launchTokens,
        string? squeezrHome,
        string launchParameterName)
    {
        RejectCmdQuote(unitName, nameof(unitName));
        foreach (var token in launchTokens)
        {
            RejectCmdQuote(token, launchParameterName);
        }

        RejectCmdQuote(squeezrHome, nameof(squeezrHome));
        foreach (var token in launchTokens)
        {
            RejectCmdPercentExpansion(token, launchParameterName);
        }

        RejectCmdPercentExpansion(squeezrHome, nameof(squeezrHome));

        var quotedTokens = string.Join(' ', launchTokens.Select(token => $"\"{token}\""));
        var run = $"{quotedTokens} {WatchdVerb} {AutostartFlag}";
        var taskCommand = squeezrHome is null
            ? run
            : $"cmd /c set \"{SqueezrHome.HomeEnvironmentVariable}={squeezrHome}\" && {run}";
        return $"/Create /TN {CmdToken(unitName)} /SC ONLOGON /TR {CmdToken(taskCommand)}";
    }

    private static string LaunchAgentPlist(string unitName, IReadOnlyList<string> launchTokens, string? squeezrHome)
    {
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            PlistDoctype,
            $"<!-- {ManagedByMarker} -->",
            "<plist version=\"1.0\">",
            "  <dict>",
            "    <key>Label</key>",
            $"    <string>{XmlText(unitName)}</string>",
            "    <key>ProgramArguments</key>",
            "    <array>",
        };
        foreach (var token in launchTokens)
        {
            lines.Add($"      <string>{XmlText(token)}</string>");
        }

        lines.AddRange(
        [
            $"      <string>{XmlText(WatchdVerb)}</string>",
            $"      <string>{XmlText(AutostartFlag)}</string>",
            "    </array>",
        ]);
        if (squeezrHome is not null)
        {
            lines.AddRange(
            [
                "    <key>EnvironmentVariables</key>",
                "    <dict>",
                $"      <key>{XmlText(SqueezrHome.HomeEnvironmentVariable)}</key>",
                $"      <string>{XmlText(squeezrHome)}</string>",
                "    </dict>",
            ]);
        }

        lines.AddRange(
        [
            "    <key>RunAtLoad</key>",
            "    <true/>",
            "    <key>KeepAlive</key>",
            "    <dict>",
            "      <key>SuccessfulExit</key>",
            "      <false/>",
            "    </dict>",
            "  </dict>",
            "</plist>",
        ]);
        return FileText(lines);
    }

    private static string FileText(IEnumerable<string> lines) => string.Join('\n', lines) + '\n';

    // The pair is the program and whatever the program needs in front of the verb — the assembly under
    // the muxer, nothing at all behind an apphost. The generator appends the verb and the autostart
    // flag itself, so a caller that hands over the whole tail gets a refusal instead of a unit that
    // execs 'watchd --autostart watchd --autostart'.
    private static List<string> LaunchTokens(WatchdRunCommand launch, string parameterName)
    {
        RequireText(launch.Executable, parameterName);
        RejectLineBreaks(launch.Executable, parameterName);
        RejectCarriedTail(launch, parameterName);
        List<string> tokens = [launch.Executable];
        foreach (var argument in launch.Arguments)
        {
            RequireText(argument, parameterName);
            RejectLineBreaks(argument, parameterName);
            tokens.Add(argument);
        }

        return tokens;
    }

    private static void RejectCarriedTail(WatchdRunCommand launch, string parameterName)
    {
        var arguments = launch.Arguments;
        if (arguments.Count >= 2 &&
            arguments[^2] == WatchdVerb &&
            arguments[^1] == AutostartFlag)
        {
            throw new ArgumentException(
                "The unit appends 'watchd --autostart' to the launch pair — the arguments carry the " +
                "tokens that come before it, not the verb and the flag themselves.",
                parameterName);
        }
    }

    // systemd.exec(5): ExecStart expands both variables ($) and specifiers (%), Environment= expands
    // specifiers only — a "$" there is literal and doubling it would corrupt the path the process sees.
    private static string SystemdQuote(string value, bool escapeDollarSign)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '$' when escapeDollarSign:
                    builder.Append("$$");
                    break;
                case '%':
                    builder.Append("%%");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string CmdToken(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static string XmlText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"The value cannot be null or white space: {parameterName}.", parameterName);
        }
    }

    private static void RejectLineBreaks(string? value, string parameterName)
    {
        if (value is not null &&
            (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Autostart unit text is line-oriented — the value must not carry a line break.", parameterName);
        }
    }

    // One level of argv escaping cannot protect a value that cmd.exe parses again: a quote here would
    // close the wrapper and the rest of the line would run as commands. "&" is safe inside the quotes.
    // Every token of the launch pair sits inside /TR, so the rule covers the muxer and its payload the
    // same way it covers a lone apphost.
    private static void RejectCmdQuote(string? value, string parameterName)
    {
        if (value is not null && value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The scheduled-task command is re-parsed by cmd.exe — the value must not carry a double quote.",
                parameterName);
        }
    }

    // cmd.exe resolves %VAR% when it builds the command line, and double quotes do not shield it — they
    // only hold the metacharacters and the spaces. A value with a percent sign would therefore name one
    // program at install time and possibly another one at logon, and there is no portable escape for it
    // inside a one-line task command, so this is a refusal instead. unitName is exempt: it goes to /TN,
    // which is schtasks' own argument and never reaches cmd.
    private static void RejectCmdPercentExpansion(string? value, string parameterName)
    {
        if (value is not null && value.Contains('%', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The scheduled-task command is expanded by cmd.exe, which resolves %VAR% even inside " +
                "double quotes — the value must not carry a percent sign.",
                parameterName);
        }
    }
}
