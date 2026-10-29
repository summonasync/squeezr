using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class ZombiePidLivenessTests
{
    [LinuxFact]
    public void IsProcessAlive_ZombiePid_IsDead()
    {
        using var holder = ZombieHolder.Spawn();
        holder.AwaitZombie();

        DaemonHeartbeatReader.IsProcessAlive(holder.ZombiePid).ShouldBeFalse();
    }

    [LinuxFact]
    public void IsAlive_FreshBeatFromZombieOwner_IsDead_WhilePresenceProbeStillSeesIt()
    {
        using var holder = ZombieHolder.Spawn();
        holder.AwaitZombie();

        Should.NotThrow(() =>
        {
            using var zombie = Process.GetProcessById(holder.ZombiePid);
        });

        var heartbeat = new DaemonHeartbeat(
            holder.ZombiePid,
            DaemonHeartbeat.DefaultKind,
            DateTime.UtcNow,
            DateTime.UtcNow);

        DaemonHeartbeatReader.IsAlive(heartbeat, DateTime.UtcNow).ShouldBeFalse();
    }
}
