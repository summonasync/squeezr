namespace Squeezr.Core.Infra.Autostart;

// The facts an autostart entry gives up when it is asked, kept apart from the verdict they lead to:
// turning them into a state also needs the platform rules for whether a file is a program the init
// system can exec, and that belongs to the CLI layer, not to the seam that only reads.
//
// UnitText is the entry as it stands on the machine, never a regeneration of what install would write
// today, so an entry that was edited by hand, half-applied or left pointing at a deleted path speaks
// for itself. It is null when there was no text to carry: absent, unreadable, or answered in a form
// this platform's reader cannot parse.
public sealed record WatchdUnitProbe(
    AutostartPlatform Platform,
    WatchdUnitAnswer Presence,
    WatchdUnitAnswer Enabled,
    WatchdUnitAnswer Failed,
    WatchdUnitAnswer Loaded,
    string? UnitText,
    IReadOnlyList<string> Notes,
    bool Probed = true)
{
    public static WatchdUnitProbe Absent(AutostartPlatform platform, string? note = null) =>
        new(
            platform,
            WatchdUnitAnswer.No,
            WatchdUnitAnswer.No,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.No,
            null,
            note is null ? [] : [note]);

    // Nothing could be asked at all: the platform's own tool is not reachable here. That is neither an
    // absent entry nor a broken one, and it must not be reported as either.
    public static WatchdUnitProbe Unprobeable(AutostartPlatform platform, string reason) =>
        new(
            platform,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unsupported,
            WatchdUnitAnswer.Unsupported,
            WatchdUnitAnswer.Unsupported,
            null,
            [reason],
            Probed: false);
}
