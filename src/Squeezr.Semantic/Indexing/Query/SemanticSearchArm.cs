using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Semantic.Embeddings;

namespace Squeezr.Semantic.Indexing.Query;

// The implementing half of Core's search seam: availability, collection size, and the resolver call
// the engine used to make inline. Failures bubble — the engine owns the fallback warning, so the arm
// stays silent about them rather than double-reporting.
public sealed record SemanticSearchArm(
    string WorkspaceRoot,
    IEmbeddingProvider Provider,
    IVectorStore Store) : ISemanticArmSource
{
    public const string VectorStoreUnavailableWarning = "vector store is unavailable, falling back to exact search only";

    public string? ModelKey => Provider.ModelKey;

    public async Task<SemanticArmResult> SearchAsync(
        SearchQuery query, CancellationToken ct, IReadOnlySet<uint>? fileScope, SecretGuardReader guard)
    {
        var warnings = new List<string>();
        if (!await Store.IsAvailableAsync(ct))
        {
            warnings.Add(VectorStoreUnavailableWarning);
            return new SemanticArmResult(SemanticArm.Empty, warnings);
        }

        if (await Store.CountAsync(ct) == 0)
        {
            return new SemanticArmResult(SemanticArm.Empty, warnings);
        }

        var arm = await SemanticHitsResolver.Resolve(
            WorkspaceRoot, query, Provider, Store, ct, fileScope, guard);
        return new SemanticArmResult(arm, warnings);
    }
}
