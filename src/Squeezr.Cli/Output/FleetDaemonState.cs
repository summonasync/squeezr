namespace Squeezr.Cli.Output;

public sealed record FleetDaemonState(
    string Status,
    int? Pid,
    long? UptimeSeconds)
{
    public const string AliveStatus = "alive";
    public const string DeadStatus = "dead";
    public const string NoneStatus = "none";

    public bool IsAlive => Status == AliveStatus;

    public static FleetDaemonState None { get; } = new(NoneStatus, null, null);

    public static FleetDaemonState Dead(int pid) => new(DeadStatus, pid, null);

    public static FleetDaemonState Alive(int pid, long uptimeSeconds) => new(AliveStatus, pid, uptimeSeconds);
}
