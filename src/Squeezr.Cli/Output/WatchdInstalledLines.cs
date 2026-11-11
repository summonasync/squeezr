namespace Squeezr.Cli.Output;

// One key=value per line, no markup, no table: the contract of this command is that another program
// can read it, so the lines are byte-stable whatever the terminal width is and a value containing a
// bracket or a space needs no escaping dance to survive. A human reads the same lines — the state
// names were chosen to be readable on their own.
//
// Every dimension line is printed on every run, including the answers this platform cannot give, so a
// missing key never has to be interpreted. program/arguments/program-state are the exception: there is
// nothing to name when the entry gave no command at all.
internal static class WatchdInstalledLines
{
    internal static IReadOnlyList<string> Of(WatchdInstalledReport report)
    {
        var probe = report.Probe;
        var lines = new List<string>
        {
            $"status={WatchdInstalledVerdict.StateKey(report.State)}",
            $"platform={WatchdInstalledVerdict.PlatformKey(probe.Platform)}",
            $"unit={report.UnitTarget}",
            $"present={WatchdInstalledVerdict.AnswerKey(probe.Presence)}",
            $"enabled={WatchdInstalledVerdict.AnswerKey(probe.Enabled)}",
            $"failed={WatchdInstalledVerdict.AnswerKey(probe.Failed)}",
            $"loaded={WatchdInstalledVerdict.AnswerKey(probe.Loaded)}",
        };

        if (report.Program is { } program)
        {
            lines.Add($"program={program.Executable}");
            if (program.Arguments.Count > 0)
            {
                lines.Add($"arguments={string.Join(' ', program.Arguments)}");
            }

            lines.Add($"program-state={report.ProgramIssue ?? "launchable"}");
        }

        lines.Add(report.Watchd.Running
            ? "watchd=running"
            : "watchd=not-running");
        if (report.Watchd.Pid is { } pid)
        {
            lines.Add($"pid={pid}");
        }

        if (WatchdInstalledVerdict.HintFor(report.State) is { } hint)
        {
            lines.Add($"hint={hint}");
        }

        foreach (var note in probe.Notes)
        {
            lines.Add($"note={note}");
        }

        return [.. lines];
    }
}
