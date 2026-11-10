using System.Text;

namespace Squeezr.Core.Infra.Autostart;

// The read side of the generator: what command does this entry actually run. It answers from the text
// that is on the machine right now, so an entry that was edited by hand, left half-applied or is
// pointing at a path that no longer exists is reported as what it is rather than as what install
// would write today. Null means the text names no command this reader can find — the caller reports
// that as a question the platform did not answer, never as a healthy entry.
public static class WatchdUnitProgram
{
    private const string ExecStartKey = "ExecStart=";
    private const string ProgramArgumentsKey = "ProgramArguments";
    private const string ProgramKey = "Program";

    // systemd.exec(5): these modifier characters in front of the executable are stripped by the
    // manager before it execs, so a hand-edited unit carrying them still names the path behind.
    private static readonly char[] SystemdExecutablePrefixes = ['-', '@', '+', '!', ':'];

    public static WatchdRunCommand? Read(AutostartPlatform platform, string? unitText)
    {
        if (string.IsNullOrWhiteSpace(unitText))
        {
            return null;
        }

        return platform switch
        {
            AutostartPlatform.SystemdUser => FromSystemd(unitText),
            AutostartPlatform.LaunchAgent => FromPlist(unitText),
            AutostartPlatform.WindowsScheduledTask => ScheduledTaskXml.ReadCommand(unitText),
            _ => null,
        };
    }

    private static WatchdRunCommand? FromSystemd(string unitText)
    {
        foreach (var line in unitText.Split('\n'))
        {
            var statement = line.Trim();
            if (!statement.StartsWith(ExecStartKey, StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = SystemdTokens(statement[ExecStartKey.Length..]);
            if (tokens.Count == 0)
            {
                return null;
            }

            var executable = tokens[0].TrimStart(SystemdExecutablePrefixes);
            return executable.Length == 0
                ? null
                : new WatchdRunCommand(executable, [.. tokens.Skip(1)]);
        }

        return null;
    }

    // Quoted runs are read as one token, and the escaper's rules are inverted one character at a
    // time: a doubled signifier is one literal character, a backslash takes what follows it as it is.
    private static List<string> SystemdTokens(string value)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var index = 0;

        while (index < value.Length)
        {
            var c = value[index];
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }

                index++;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                index++;
                continue;
            }

            token.Append(SystemdUnescaped(value, ref index));
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    private static char SystemdUnescaped(string value, ref int index)
    {
        var c = value[index];
        if (c == '\\' && index + 1 < value.Length)
        {
            var escaped = value[index + 1];
            index += 2;
            return escaped;
        }

        if ((c == '$' || c == '%') && index + 1 < value.Length && value[index + 1] == c)
        {
            index += 2;
            return c;
        }

        index++;
        return c;
    }

    private static WatchdRunCommand? FromPlist(string plistText)
    {
        var root = UnitXml.Parse(plistText)?.Root?.Element("dict");
        if (root is null)
        {
            return null;
        }

        string? key = null;
        foreach (var entry in root.Elements())
        {
            if (entry.Name.LocalName == "key")
            {
                key = entry.Value;
                continue;
            }

            if (key == ProgramArgumentsKey && entry.Name.LocalName == "array")
            {
                List<string> values = [.. entry.Elements("string").Select(value => value.Value)];
                return values.Count == 0
                    ? null
                    : new WatchdRunCommand(values[0], values[1..]);
            }

            if (key == ProgramKey && entry.Name.LocalName == "string")
            {
                return new WatchdRunCommand(entry.Value, []);
            }
        }

        return null;
    }
}
