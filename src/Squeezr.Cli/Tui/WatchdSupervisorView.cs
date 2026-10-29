namespace Squeezr.Cli.Tui;

public sealed record WatchdSupervisorView(WatchdDaemonStatus Daemon, IReadOnlyList<WatchdSessionRow> Sessions)
{
    public WatchdSessionRow? Find(string storeKey) =>
        Sessions.FirstOrDefault(session => session.StoreKey == storeKey);
}
