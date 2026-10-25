namespace Squeezr.Core.Indexing.Watch;

internal sealed class WatchActivityProgress(DaemonHeartbeatWriter heartbeat, IIndexProgress? inner) : IIndexProgress
{
    public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
    {
        heartbeat.PhaseStarted(repoName, phase, totalItems);
        inner?.PhaseStarted(repoName, phase, totalItems);
    }

    public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
    {
        heartbeat.ItemProcessed(repoName, currentItem);
        inner?.ItemProcessed(repoName, currentItem, symbolsInserted);
    }

    public void RepoCompleted(string repoName)
    {
        heartbeat.RepoCompleted(repoName);
        inner?.RepoCompleted(repoName);
    }
}
