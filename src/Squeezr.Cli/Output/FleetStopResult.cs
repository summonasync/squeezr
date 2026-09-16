namespace Squeezr.Cli.Output;

public sealed record FleetStopResult(
    string Name,
    string StoreKey,
    IReadOnlyList<FleetStopKindResult> Results);
