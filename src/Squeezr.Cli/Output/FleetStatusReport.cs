namespace Squeezr.Cli.Output;

public sealed record FleetStatusReport(IReadOnlyList<FleetStatusEntry> Stores, FleetWatchdState Watchd);
