using Squeezr.Core.Embeddings;

namespace Squeezr.Core.Workspace;

// The semantic runtime the reconcile pass runs with, handed over by the caller that knows how to build one.
// A pass asked to reconcile a lane whose provider is not in the room is a legitimate shape: the coordinates
// still land, the embeddings wait, and the reason is reported rather than guessed at.
public sealed record WorktreeReconcileRuntime(
    IEmbeddingProvider? Embeddings,
    IVectorStore? VectorStore,
    EmbeddingProviderResolution? ProviderResolution,
    string? EmbeddingsUnavailableReason,
    IReadOnlyList<string>? Warnings = null);
