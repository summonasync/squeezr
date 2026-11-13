namespace Squeezr.Core.Workspace;

// The two ceilings one reconcile session may spend asking the collection about the store's claim, and the
// reason each sits where it does. The unit is the point id, because that is the unit a claim and a
// collection share: this store's rows call N chunk hashes embedded, and N ids answer for them.
//
// Measured 2026-11-13 on this product's own repository at HEAD: 3 939 distinct embedded chunk hashes over
// src/, which is what the review-116-11 §3 cost clause counted as ~8 pages of the 500-point walk WorktreeReconcile
// used to run twice per session. That number sets the scale for both ceilings below.
//
// AttestationSampleIds is what a lane that owes nothing pays: one round trip asking for a spread of the ids
// its own claim names. It is not a coverage measurement and is never reported as one — a store's proof was
// taken whole by the pass that completed it, and this sample only asks whether the collection still holds
// ids this product wrote. It catches the classes that actually happen: an empty collection after a drop or a
// re-created service answers zero of the sample, and a collection swapped for another store's answers zero
// too, which is the exact failure review-116-10 measured a count comparison printing as reconciled.
//
// FullClaimIdCeiling bounds the whole measurement a pass owes. Over the ceiling the claim is not weighed
// against anything and is not called covered: the pass reports that it did not measure, which is the shape
// the pending takes when a service does not answer, and never the shape a cardinality comparison would have
// handed back.
public static class ReconcileProbeBudget
{
    public const int AttestationSampleIds = 32;

    public const int FullClaimIdCeiling = 50_000;
}
