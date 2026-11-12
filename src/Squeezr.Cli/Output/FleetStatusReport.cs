namespace Squeezr.Cli.Output;

// StateFault is the state file's own voice when it cannot be read: null says the desire behind every row
// is what the file actually records, a fault sentence says no row's desire is known and the supervision
// cells carry live facts only.
public sealed record FleetStatusReport(
    IReadOnlyList<FleetStatusEntry> Stores,
    FleetWatchdState Watchd,
    string? StateFault = null);
