namespace Squeezr.Semantic.Health;

public sealed record EmbeddingsHealth(
    string? Provider,
    string? ModelProblem,
    string? CpuOnly,
    string? ModelKey = null,
    int? OtherModelChunks = null)
{
    public static readonly EmbeddingsHealth Silent = new(null, null, null);
}
