namespace Squeezr.Core.Indexing;

public interface IIndexProgress
{
    void PhaseStarted(string repoName, IndexPhase phase, int totalItems);

    void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null);

    void RepoCompleted(string repoName);
}
