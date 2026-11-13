namespace Squeezr.Cli.Tui;

public enum WatchdSessionState
{
    Running,
    Starting,
    Stopped,
    Wedged,
    ManagedElsewhere,
    NoSupervisor,
}
