namespace Squeezr.Core.Workspace;

// What a store knows about itself, written where the store lives. The copy plants another checkout's rows
// and the reconcile pass is what turns them into THIS tree's coordinates, so the store has to carry the
// difference between 'a pass over this root finished' and 'the numbers I copied happen to agree with each
// other' — until now nothing persisted that, and every healthy session start paid a measurement to re-ask
// a question it could have remembered (review-116-11 §3) while a lane whose pass was killed could still read
// as fresh (review-116-08's polarity ruling).
//
// Three questions, three fields, and none of them folded into the next: whether the store was ever filled,
// whether the exact arm came with the copy (a parent without a trigram.idx yields a child that keeps its
// rows and permanently lacks exact search, so the content of meta.db alone cannot tell a filled store from
// a partial one), and whether a pass over this root has completed. The semantic answer is a third state
// rather than a bool because 'the collection did not answer' is not the same fact as 'the collection is
// short', and only the second one is a gap anyone can act on.
//
// The field is additive: a manifest written before this rule has no block, and a store that never got one
// is a store no copy ever filled — read as not-pending (035).
public sealed record ReconcileMarker(
    bool Filled,
    bool? ExactArmLanded,
    bool PassCompleted,
    bool? SemanticArmLanded,
    int ObligationChunks,
    int? CoveredChunks,
    string? Code,
    DateTimeOffset WrittenAt)
{
    // The state a fill opens before it moves a byte: pending is recorded first, so a pass that dies between
    // the copy and the marker update leaves a store that cannot read as earned.
    public static ReconcileMarker Opened() =>
        new(Filled: false, ExactArmLanded: null, PassCompleted: false, SemanticArmLanded: null, 0, null, null,
            DateTimeOffset.UtcNow);

    // The fill landed files and reported whether the trigram came with them. The semantic claim stays
    // unmeasured here: the collection answers to the pass, not to the copy.
    public ReconcileMarker FilledFrom(bool exactArmLanded, string? code) =>
        this with { Filled = true, ExactArmLanded = exactArmLanded, Code = code, WrittenAt = DateTimeOffset.UtcNow };

    // The pass's answer, in the unit the claim is made of. 'covered' stays null when the measurement did not
    // answer, because an unanswered measurement is neither a gap nor a proof. 'filled' is taken from the rows
    // the pass read when the record itself has no copy to point at — a lane whose rows landed before this
    // rule existed is filled, and saying otherwise would be a claim about a byte this door can already count.
    public ReconcileMarker ClosedPass(
        bool passCompleted,
        bool? semanticArmLanded,
        int obligationChunks,
        int? coveredChunks,
        string? code,
        bool? exactArmLanded = null,
        bool? filled = null) =>
        this with
        {
            Filled = filled ?? Filled,
            PassCompleted = passCompleted,
            SemanticArmLanded = semanticArmLanded,
            ObligationChunks = obligationChunks,
            CoveredChunks = coveredChunks,
            Code = code,
            ExactArmLanded = exactArmLanded ?? ExactArmLanded,
            WrittenAt = DateTimeOffset.UtcNow,
        };

    // The predicate every surface reads: a store that was filled owes its reader the pending word until a
    // pass over this root has completed AND the vectors its rows claim are accounted for. A store that
    // claims no vector is accounted for by having nothing to account for.
    public bool Pending => !PassCompleted || SemanticArmLanded != true;

    // The coordinates half on its own: what a per-file verdict may not claim while it is missing. A lane
    // whose embeddings are still queued answers exact coordinates, and the freshness layer owns its drift.
    public bool CoordinatesProven => PassCompleted;
}
