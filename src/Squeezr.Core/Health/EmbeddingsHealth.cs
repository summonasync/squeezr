namespace Squeezr.Core.Health;

public sealed record EmbeddingsHealth(
    string? Provider,
    string? FallbackReason,
    string? ModelProblem,
    string? CpuOnly)
{
    public static readonly EmbeddingsHealth Silent = new(null, null, null, null);
}
