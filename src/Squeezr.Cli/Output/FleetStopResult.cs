namespace Squeezr.Cli.Output;

public sealed record FleetStopResult(
    string Name,
    string StoreKey,
    IReadOnlyList<FleetStopKindResult> Results,
    // Additive since 070/T5: live beats on the store that own nothing — fleet stop leaves them running, and
    // an honest report says who it did not touch.
    int Observers = 0,
    // Additive since 108/T2: a stop is an intention now, not just a kill — the recorded desire is part
    // of the answer.
    string Desired = "stopped");
