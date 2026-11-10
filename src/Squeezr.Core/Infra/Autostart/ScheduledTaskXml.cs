namespace Squeezr.Core.Infra.Autostart;

// The three questions a task-XML answer can reply to. Every reading here is deliberately narrow: an
// element the export does not carry answers "unknown" rather than being guessed at from the schema
// default, because this shape has not been measured against a live Task Scheduler in this round and
// a diagnostic that fills gaps with defaults is how an absent entry starts being reported as a good
// one. Windows owners: a wrong reading shows up as enable=couldn't-ask in 'watchd installed', which
// is the place to report it.
internal static class ScheduledTaskXml
{
    private const string ExecElement = "Exec";
    private const string CommandElement = "Command";
    private const string ArgumentsElement = "Arguments";
    private const string SettingsElement = "Settings";
    private const string EnabledElement = "Enabled";
    private const string RuntimeInfoElement = "RuntimeInfo";
    private const string LastTaskResultElement = "LastTaskResult";

    // schtasks reports "not yet run" as its own result code; reading it as a failure would invent a
    // broken task out of a freshly installed one.
    private const int NotYetRunResult = 267009;

    internal static WatchdRunCommand? ReadCommand(string? taskXml)
    {
        var exec = UnitXml.Parse(taskXml) is { } document
            ? UnitXml.LocalDescendants(document, ExecElement).FirstOrDefault()
            : null;
        if (exec is null)
        {
            return null;
        }

        var command = UnitXml.Value(exec, CommandElement);
        var arguments = UnitXml.Value(exec, ArgumentsElement);
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var line = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";
        return CommandLine(UnwrapCmd(line));
    }

    // The flag is read under Settings, not from anywhere in the document: a trigger element carries an
    // Enabled of its own, and a reader that takes the first one it finds reports whether the logon
    // trigger is on rather than whether the task is.
    internal static WatchdUnitAnswer ReadEnablement(string? taskXml)
    {
        var enabled = UnitXml.Parse(taskXml) is { } document
            ? UnitXml.LocalDescendants(document, SettingsElement)
                .Select(settings => UnitXml.Value(settings, EnabledElement))
                .FirstOrDefault(value => value is not null)
            : null;
        if (enabled is null)
        {
            return WatchdUnitAnswer.Unknown;
        }

        // The schema types this flag as xs:boolean, whose legal spellings are four, not the two words a
        // word-only reader knows: a disabled task exported as <Enabled>0</Enabled> must not come back as
        // "no answer", because an unanswered question is one the verdict may walk past. The word arm
        // keeps the casing a live export uses (<Enabled>TRUE</Enabled>).
        var flag = enabled.Trim();
        return flag switch
        {
            "true" or "1" => WatchdUnitAnswer.Yes,
            "false" or "0" => WatchdUnitAnswer.No,
            _ when bool.TryParse(flag, out var word) => word
                ? WatchdUnitAnswer.Yes
                : WatchdUnitAnswer.No,
            _ => WatchdUnitAnswer.Unknown,
        };
    }

    // The last run outcome only exists once the task has run: no RuntimeInfo at all, or no result
    // inside it, is the platform having nothing to say about failure — not a healthy run and not a
    // broken one.
    internal static WatchdUnitAnswer ReadFailure(string? taskXml)
    {
        var outcome = UnitXml.Parse(taskXml) is { } document
            ? UnitXml.LocalDescendants(document, RuntimeInfoElement)
                .Select(runtime => UnitXml.Value(runtime, LastTaskResultElement))
                .FirstOrDefault(value => value is not null)
            : null;
        if (outcome is null)
        {
            return WatchdUnitAnswer.Unsupported;
        }

        // Some locales print the result in hex; a value this reader cannot turn into a number is not an
        // answer about the run, and saying so is the honest remainder.
        return int.TryParse(outcome.Trim(), out var result)
            ? result switch
            {
                0 => WatchdUnitAnswer.No,
                NotYetRunResult => WatchdUnitAnswer.Unsupported,
                _ => WatchdUnitAnswer.Yes,
            }
            : WatchdUnitAnswer.Unknown;
    }

    // The stored command is either the program itself or the cmd wrapper the generator builds to
    // carry a non-default SQUEEZR_HOME; the program the task really starts sits behind the last
    // quoted "&&". A quote never reaches here at all — the generator refuses one — so the closing
    // quote of the set assignment is the only place where " && " can follow a quote.
    private static string UnwrapCmd(string line)
    {
        var shell = Path.GetFileName(line.Split(' ', 2, StringSplitOptions.TrimEntries)[0].Trim('"'))
            .ToLowerInvariant();
        if (shell != "cmd" && shell != "cmd.exe")
        {
            return line;
        }

        var boundary = line.LastIndexOf("\" && ", StringComparison.Ordinal);
        return boundary < 0 ? line : line[(boundary + 4)..];
    }

    internal static WatchdRunCommand? CommandLine(string line)
    {
        var tokens = new List<string>();
        var token = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }

                continue;
            }

            token.Append(c);
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens.Count == 0
            ? null
            : new WatchdRunCommand(tokens[0], [.. tokens.Skip(1)]);
    }
}
