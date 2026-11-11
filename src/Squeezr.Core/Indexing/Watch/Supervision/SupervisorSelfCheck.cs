namespace Squeezr.Core.Indexing.Watch.Supervision;

// What the supervisor knows about itself when something else started it. Retiring on an upgrade is
// only ever safe where the starter brings the process back, so the entry's restart behavior travels
// with the check instead of being assumed by the loop; a null start time means the comparison cannot
// be made and the supervisor keeps running the code it already has.
public sealed record SupervisorSelfCheck(
    string BinaryPath,
    DateTime? ProcessStartUtc,
    bool RestartsOnUnsuccessfulExit);
