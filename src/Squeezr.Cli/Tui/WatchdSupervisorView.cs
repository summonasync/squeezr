namespace Squeezr.Cli.Tui;

// StateFault names the state file's own read failure; null means every row's desire came from the file.
public sealed record WatchdSupervisorView(
    WatchdDaemonStatus Daemon,
    IReadOnlyList<WatchdSessionRow> Sessions,
    string? StateFault = null)
{
    public WatchdSessionRow? Find(string storeKey) =>
        Sessions.FirstOrDefault(session => session.StoreKey == storeKey);
}
