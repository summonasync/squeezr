using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

// The vector lifecycle of an index pass, owned as a contract here and implemented by the assembly
// that holds the actual stores and providers. A null phase is a legitimate shape: the pass runs
// exact-only, and every point-related branch is skipped or served from the reap journal instead.
public interface IIndexVectorPhase
{
    string ExecutorDevice { get; }

    string ProviderName { get; }

    void DropCollection(List<string> warnings);

    void Preflight(List<string> warnings);

    void ReplayJournal(MetaStore store, List<string> warnings);

    int RetirePoints(MetaStore store, IReadOnlyList<PendingReapTarget> files, List<string> warnings, SecretGuardIngestion guard);

    (int Embedded, bool Completed) DrainRepo(
        RepoDocument repo, MetaStore store, SqueezrConfig config, List<string> warnings, IIndexProgress? progress, int? budget);

    void DrainFile(RepoDocument repo, MetaStore store, SqueezrConfig config, List<string> warnings);
}
