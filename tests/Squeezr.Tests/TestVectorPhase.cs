using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Semantic.Indexing.Query;
using Squeezr.Semantic.Embeddings;
using Squeezr.Semantic.Indexing.Watch;

namespace Squeezr.Tests;

// The seam hand test call sites used to spell out: a provider and a store pair become one phase, or
// the pass runs exact-only. Null on either side stays a legitimate shape here exactly as it is upstream.
public static class TestVectorPhase
{
    public static IIndexVectorPhase? Runtime(IEmbeddingProvider? provider, IVectorStore? store, EmbeddingProviderResolution? resolution = null) =>
        provider is null || store is null ? null : new WatchEmbeddingRuntime(provider, store, resolution);

    public static ISemanticArmSource? Arm(string workspaceRoot, IEmbeddingProvider? provider, IVectorStore? store) =>
        provider is null || store is null ? null : new SemanticSearchArm(workspaceRoot, provider, store);
}
