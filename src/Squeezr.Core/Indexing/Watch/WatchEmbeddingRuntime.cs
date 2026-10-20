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
    public string ProviderName => Resolution?.Provider ?? "unknown";
}
