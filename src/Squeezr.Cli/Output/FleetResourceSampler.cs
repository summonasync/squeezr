namespace Squeezr.Cli.Output;

public delegate IReadOnlyDictionary<int, DaemonResourceSample> FleetResourceSampler(
    IReadOnlyCollection<int> pids,
    TimeSpan window);
