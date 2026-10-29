namespace Squeezr.Core.Indexing.Watch.Supervision;

public interface IWatchDaemonLauncher
{
    IWatchDaemonHandle StartWatch(string rootPath);
}
