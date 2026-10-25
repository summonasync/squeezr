using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Watch;

// Built once per serve process and handed to the watcher as an already-resolved task: provider
// construction may load a model or probe a resolver endpoint, so it must never sit on the serve
// startup path. The resolution rides along because the pass derives its model key from the config
// provider, which may still say "auto" — an unresolved auto would make every pass throw. A null
// result means the drain is off for this serve — passes stay embed-free.
public sealed record WatchEmbeddingRuntime(
    IEmbeddingProvider Provider,
    IVectorStore Store,
    EmbeddingProviderResolution? Resolution)
{
    private EmbeddingExecutor? _executor;

    public string ProviderName => Resolution?.Provider ?? "unknown";

    public EmbeddingExecutor Executor => _executor ??= Provider is IEmbeddingExecutorEvidence evidenced
        ? ProbeEvidenced(evidenced)
        : new EmbeddingExecutor("unknown", $"provider '{ProviderName}' offers no executor evidence");

    // The seam owns the failure: an evidence source is expected to degrade on its own, but a future one
    // that throws must cost the verdict, never the pass that reads it.
    private static EmbeddingExecutor ProbeEvidenced(IEmbeddingExecutorEvidence evidenced)
    {
        try
        {
            return evidenced.ProbeExecutor();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new EmbeddingExecutor("unknown", $"executor evidence failed: {ex.Message}");
        }
    }
}
