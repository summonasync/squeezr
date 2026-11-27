using Squeezr.Core.Workspace;

using Squeezr.Semantic.Workspace;

namespace Squeezr.Semantic.Health;

public sealed record WorkspaceSemanticFacts(QdrantStatus? Qdrant, EmbeddingsHealth Health, string? UnavailableReason = null)
{
    public static WorkspaceSemanticFacts Silent { get; } = new(null, EmbeddingsHealth.Silent);
}
