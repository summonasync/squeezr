namespace Squeezr.Cli.Output;

public sealed record FleetStartResult(
    string Name,
    string StoreKey,
    string Desired,
    FleetWatchdState Watchd,
    string IndexerState);
