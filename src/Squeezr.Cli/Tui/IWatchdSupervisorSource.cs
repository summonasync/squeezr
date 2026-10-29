namespace Squeezr.Cli.Tui;

public interface IWatchdSupervisorSource
{
    WatchdSupervisorView Read();

    bool IsWatcherRunning(string storeKey);
}
