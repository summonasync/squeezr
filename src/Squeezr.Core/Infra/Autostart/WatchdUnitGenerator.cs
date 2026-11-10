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

    private const string UnitDescription = "squeezr watch daemon";
    private const string PlistDoctype =
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">";

    public static string Generate(
        AutostartPlatform platform,
        string binaryPath,
        string? squeezrHome = null,
        string? unitName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        RejectLineBreaks(binaryPath, nameof(binaryPath));
        RejectLineBreaks(squeezrHome, nameof(squeezrHome));
        RejectLineBreaks(unitName, nameof(unitName));

        var name = string.IsNullOrWhiteSpace(unitName) ? DefaultUnitNameFor(platform) : unitName;
        var home = string.IsNullOrWhiteSpace(squeezrHome) ? null : squeezrHome;

        return platform switch
        {
            AutostartPlatform.SystemdUser => SystemdUnit(name, binaryPath, home),
            AutostartPlatform.WindowsScheduledTask => SchtasksArguments(name, binaryPath, home),
            AutostartPlatform.LaunchAgent => LaunchAgentPlist(name, binaryPath, home),
            _ => throw new ArgumentOutOfRangeException(
                nameof(platform), platform, $"Unknown autostart platform {(int)platform}."),
        };
    }

    public static WatchdRunCommand RunCommand(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        RejectLineBreaks(binaryPath, nameof(binaryPath));
        return new WatchdRunCommand(binaryPath, [WatchdVerb, AutostartFlag]);
    }

    private static string DefaultUnitNameFor(AutostartPlatform platform) =>
        platform == AutostartPlatform.LaunchAgent ? LaunchAgentUnitName : DefaultUnitName;

    private static string SystemdUnit(string unitName, string binaryPath, string? squeezrHome)
    {
        var lines = new List<string>
        {
            $"# {unitName}.service — {ManagedByMarker}",
            "[Unit]",
            $"Description={UnitDescription}",
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
            $"ExecStart={SystemdQuote(binaryPath, escapeDollarSign: true)} {WatchdVerb} {AutostartFlag}",
            "Restart=on-failure",
            "RestartSec=5",
            string.Empty,
            "[Install]",
            "WantedBy=default.target",
        ]);
        return FileText(lines);
    }

    private static string SchtasksArguments(string unitName, string binaryPath, string? squeezrHome)
    {
        RejectCmdQuote(unitName, nameof(unitName));
        RejectCmdQuote(binaryPath, nameof(binaryPath));
        RejectCmdQuote(squeezrHome, nameof(squeezrHome));
        RejectCmdPercentExpansion(binaryPath, nameof(binaryPath));
        RejectCmdPercentExpansion(squeezrHome, nameof(squeezrHome));

        var run = $"\"{binaryPath}\" {WatchdVerb} {AutostartFlag}";
        var taskCommand = squeezrHome is null
            ? run
            : $"cmd /c set \"{SqueezrHome.HomeEnvironmentVariable}={squeezrHome}\" && {run}";
        return $"/Create /TN {CmdToken(unitName)} /SC ONLOGON /TR {CmdToken(taskCommand)}";
    }

    private static string LaunchAgentPlist(string unitName, string binaryPath, string? squeezrHome)
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
            $"      <string>{XmlText(binaryPath)}</string>",
            $"      <string>{XmlText(WatchdVerb)}</string>",
            $"      <string>{XmlText(AutostartFlag)}</string>",
            "    </array>",
        };
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
