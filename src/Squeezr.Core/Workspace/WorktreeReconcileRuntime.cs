using Squeezr.Core.Indexing;

namespace Squeezr.Core.Workspace;

// The collection half of a reconcile pass, handed over by the caller that knows how to build one.
// A pass asked to reconcile a lane whose provider is not in the room is a legitimate shape: the
// coordinates still land, the vectors wait, and the reason is reported rather than guessed at.
public sealed record WorktreeReconcileRuntime(
    IIndexVectorPhase? VectorPhase,
    IWorktreeVectorProbe? VectorProbe,
    string? SemanticUnavailableReason,
    IReadOnlyList<string>? Warnings = null);
