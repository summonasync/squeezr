namespace Squeezr.Core.Workspace;

// What a measurement of the collection found, in the claim's own unit. Whether a reader may take these
// numbers as this call's measurement or only as an attestation of the record is not carried here — the
// outcome's 'basis' says it, and a sample never answers how much of the claim the collection reaches.
public sealed record VectorClaimCheck(long? Points, int? Uncovered, string? Failure, int Probed)
{
    public bool Gap => Uncovered is > 0;

    public bool Answered => Failure is null;

    // The shape a store with no vector claim is measured against: nothing was asked, so nothing is
    // reported, and the verdict comes from the rows alone.
    public static VectorClaimCheck NothingToAsk() => new(null, null, null, Probed: 0);
}

// The collection half of a worktree reconcile, asked in chunk hashes — the ids those hashes name are the
// implementing assembly's business, so the claim is weighed here without Core ever deriving one.
public interface IWorktreeVectorProbe
{
    VectorClaimCheck Measure(IReadOnlyList<string> chunkHashes, CancellationToken ct);

    VectorClaimCheck Attest(IReadOnlyList<string> chunkHashes, CancellationToken ct);
}
