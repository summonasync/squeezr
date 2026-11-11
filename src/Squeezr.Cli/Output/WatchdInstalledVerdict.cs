using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Cli.Output;

// The stable vocabulary of the diagnostic: the state names an operator scripts against, the answer
// words that name each dimension without lying about it, and the exit-code ladder.
//
// Exit codes, one step apart and never overlapping:
//   0 installed              — the entry is there, hooked, and names a program this platform can start
//   1 not-installed          — a clean answer: nothing is registered
//   2 broken                 — registered, and it will not start the daemon as it stands
//   3 no verdict             — the platform refused to answer, or has no question to ask here
internal static class WatchdInstalledVerdict
{
    internal const int Healthy = 0;
    internal const int NotRegistered = 1;
    internal const int Broken = 2;
    internal const int NoVerdict = 3;

    internal static string StateKey(WatchdAutostartState state) => state switch
    {
        WatchdAutostartState.Installed => "installed",
        WatchdAutostartState.NotInstalled => "not-installed",
        WatchdAutostartState.InstalledButBinaryMissing => "installed-but-binary-missing",
        WatchdAutostartState.UnitFailed => "unit-failed",
        WatchdAutostartState.PresentButNotEnabled => "present-but-not-enabled",
        WatchdAutostartState.ProbeUnsupported => "probe-unsupported",
        // A state this reader does not know is not a healthy one, and it is not a missing entry
        // either: it says what it is and stops there.
        _ => "probe-unknown",
    };

    internal static int ExitCodeFor(WatchdAutostartState state) => state switch
    {
        WatchdAutostartState.Installed => Healthy,
        WatchdAutostartState.NotInstalled => NotRegistered,
        WatchdAutostartState.InstalledButBinaryMissing
            or WatchdAutostartState.UnitFailed
            or WatchdAutostartState.PresentButNotEnabled => Broken,
        _ => NoVerdict,
    };

    internal static string? HintFor(WatchdAutostartState state) => state switch
    {
        WatchdAutostartState.NotInstalled =>
            "run 'squeezr watchd install' to register autostart for this account",
        WatchdAutostartState.PresentButNotEnabled =>
            "the entry is only half applied — run 'squeezr watchd install' to re-register it",
        WatchdAutostartState.InstalledButBinaryMissing =>
            "the entry starts a program that is not launchable — re-run 'squeezr watchd install' " +
            "from the installed squeezr binary",
        WatchdAutostartState.UnitFailed =>
            "the platform reports the entry as failed — look it up with the platform tool, " +
            "then re-run 'squeezr watchd install'",
        _ => null,
    };

    internal static string AnswerKey(WatchdUnitAnswer answer) => answer switch
    {
        WatchdUnitAnswer.Yes => "yes",
        WatchdUnitAnswer.No => "no",
        WatchdUnitAnswer.Unknown => "couldn't-ask",
        _ => "not-supported",
    };

    internal static string PlatformKey(AutostartPlatform platform) => platform switch
    {
        AutostartPlatform.SystemdUser => "systemd-user",
        AutostartPlatform.LaunchAgent => "launch-agent",
        AutostartPlatform.WindowsScheduledTask => "windows-task",
        _ => "unknown",
    };
}
