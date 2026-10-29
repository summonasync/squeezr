using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Tui;

internal static class WatchdSessionText
{
    public static string State(WatchdSessionState state) => state switch
    {
        WatchdSessionState.Running => "running",
        WatchdSessionState.Stopped => "stopped",
        WatchdSessionState.Wedged => "wedged",
        _ => "managed elsewhere",
    };

    public static string Desire(WatchDesire desire) => desire.ToString().ToLowerInvariant();
}
