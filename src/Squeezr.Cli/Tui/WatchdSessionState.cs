namespace Squeezr.Cli.Tui;

public enum WatchdSessionState
{
    Running,
    Stopped,
    Wedged,
    ManagedElsewhere,
    NoSupervisor,
}
