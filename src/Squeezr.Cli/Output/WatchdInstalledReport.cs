using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Cli.Output;

// Everything 'watchd installed' can say about one autostart entry: the answers the platform gave, the
// state those answers add up to, and the command the entry names. The state is resolved once, here as
// data, so the exit code, the printed keys and the tests all read the same verdict.
public sealed record WatchdInstalledReport(
    WatchdAutostartState State,
    WatchdUnitProbe Probe,
    string UnitTarget,
    WatchdRunCommand? Program,
    string? ProgramIssue,
    FleetWatchdState Watchd);
