using Squeezr.Core.Workspace;

namespace Squeezr.Core.Health;

public sealed record WorkspaceSemanticFacts(QdrantStatus? Qdrant, EmbeddingsHealth Health, string? UnavailableReason = null)
{
    public static WorkspaceSemanticFacts Silent { get; } = new(null, EmbeddingsHealth.Silent);
}
