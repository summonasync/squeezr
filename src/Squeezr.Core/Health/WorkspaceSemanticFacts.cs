using Squeezr.Core.Workspace;

namespace Squeezr.Core.Health;

public sealed record WorkspaceSemanticFacts(QdrantStatus? Qdrant, EmbeddingsHealth Health)
{
    public static WorkspaceSemanticFacts Silent { get; } = new(null, EmbeddingsHealth.Silent);
}
