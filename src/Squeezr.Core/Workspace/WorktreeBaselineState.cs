namespace Squeezr.Core.Workspace;

// Whether the lane's store ended up owning the baseline repo. `Skipped` is the state where the flag was asked
// and no repo could be registered because this pass created no store to register it in — the answer a reader
// needs as a field, because a run that did half of what was asked and said nothing is how a flag starts lying.
// Every other refusal this door can hit is a `WorkspaceException` with its own code: a lane that asks for a
// second tree and is refused loudly is not a state a report should carry quietly.
public enum WorktreeBaselineState
{
    Registered,
    AlreadyRegistered,
    Skipped,
}
