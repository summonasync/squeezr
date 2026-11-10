using Squeezr.Cli.Output;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Cli.Infra.Autostart;

// The fold from answers to verdict. It is deliberately ordered by what an operator can act on: a
// refusal to answer outranks everything, a failed entry outranks a half-applied one, and a missing
// program is only reported as such once the entry is known to be hooked in — an unhooked unit that
// also names a deleted binary is still the half-install that has to be re-run.
internal static class WatchdInstalledProbe
{
    internal static async Task<WatchdInstalledReport> ReadAsync(
        IWatchdAutostartApplier applier,
        FleetWatchdState watchd,
        CancellationToken cancellationToken)
    {
        var probe = await applier.ProbeAsync(cancellationToken);
        var program = WatchdUnitProgram.Read(probe.Platform, probe.UnitText);
        var issue = program is null ? null : ProgramIssue(probe.Platform, program);
        var state = Resolve(probe, program, issue);
        if (program is null && probe.Presence == WatchdUnitAnswer.Yes)
        {
            probe = probe with
            {
                Notes = [.. probe.Notes, "the entry carries no command this reader could identify"],
            };
        }

        return new WatchdInstalledReport(state, probe, applier.ManagedTarget, program, issue, watchd);
    }

    private static WatchdAutostartState Resolve(
        WatchdUnitProbe probe,
        WatchdRunCommand? program,
        string? issue)
    {
        if (!probe.Probed)
        {
            return WatchdAutostartState.ProbeUnsupported;
        }

        if (probe.Presence == WatchdUnitAnswer.Unknown)
        {
            return WatchdAutostartState.ProbeUnknown;
        }

        if (probe.Presence == WatchdUnitAnswer.No)
        {
            return WatchdAutostartState.NotInstalled;
        }

        if (probe.Failed == WatchdUnitAnswer.Yes)
        {
            return WatchdAutostartState.UnitFailed;
        }

        if (probe.Enabled == WatchdUnitAnswer.No)
        {
            return WatchdAutostartState.PresentButNotEnabled;
        }

        if (program is null)
        {
            return WatchdAutostartState.ProbeUnknown;
        }

        if (issue is not null)
        {
            return WatchdAutostartState.InstalledButBinaryMissing;
        }

        // The failure question is open on a platform that asks it: the entry may well be sitting in
        // the crash-loop state the unit's own Restart=on-failure walks into, and calling that
        // healthy would be the exact lie this command exists to stop.
        return probe.Failed == WatchdUnitAnswer.Unknown
            ? WatchdAutostartState.ProbeUnknown
            : WatchdAutostartState.Installed;
    }

    // The same rules install refuses to write under, read as an answer. A host program with a payload
    // path in front of it is broken when the payload is gone as much as when the host is — that is
    // the shape the muxer form puts into ExecStart, and the verbs a plain unit carries are not paths.
    private static string? ProgramIssue(AutostartPlatform platform, WatchdRunCommand program)
    {
        var refusal = Launchability.Refusal(platform, program.Executable);
        if (refusal is not null)
        {
            return refusal;
        }

        foreach (var argument in program.Arguments)
        {
            if (!argument.StartsWith('-') &&
                Path.IsPathRooted(argument) &&
                !File.Exists(argument))
            {
                return $"the path '{argument}' the entry passes to the program does not exist";
            }
        }

        return null;
    }
}
