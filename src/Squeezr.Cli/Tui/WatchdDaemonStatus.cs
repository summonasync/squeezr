namespace Squeezr.Cli.Tui;

public sealed record WatchdDaemonStatus(bool Running, int? OwnerPid)
{
    public static readonly WatchdDaemonStatus NotRunning = new(false, null);
}
