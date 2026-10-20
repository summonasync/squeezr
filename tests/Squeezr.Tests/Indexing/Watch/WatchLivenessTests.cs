using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public class WatchLivenessTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 18, 54, 0, DateTimeKind.Utc);
    private const int HolderPid = 69168;

    [Fact]
    public void FreshHeartbeat_WithoutPassStamp_IsIdle()
    {
        var verdict = WatchLiveness.Classify(Heartbeat(secondsAgo: 5), pass: null, Now, Alive);

        verdict.State.ShouldBe(WatchLivenessState.Idle);
    }

    [Fact]
    public void FreshHeartbeat_WithStuckPass_IsStuck_EvenThoughHeartbeatIsAlive()
    {
        var heartbeat = Heartbeat(secondsAgo: 5);
        var wedgeStart = Now.AddMinutes(-95);
        var pass = new PassPhaseStamp(HolderPid, PassPhaseStamp.StuckPhase, wedgeStart, wedgeStart, 3);

        DaemonHeartbeatReader.IsAlive(heartbeat, Now, Alive).ShouldBeTrue();

        var verdict = WatchLiveness.Classify(heartbeat, pass, Now, Alive);

        // the 2026-10-03 wedge: the daemon beat every 5s for 95 minutes while its pass sat blocked —
        // liveness must name that state instead of reporting a healthy daemon
        verdict.State.ShouldBe(WatchLivenessState.Stuck);
        verdict.Detail.ShouldContain("stuck");
        verdict.Detail.ShouldContain("no progress");
    }

    [Fact]
    public void FreshHeartbeat_WithRunningPass_IsWorking()
    {
        var pass = new PassPhaseStamp(
            HolderPid, PassPhaseStamp.RunningPhase, Now.AddSeconds(-40), Now.AddSeconds(-10), 12);

        var verdict = WatchLiveness.Classify(Heartbeat(secondsAgo: 5), pass, Now, Alive);

        verdict.State.ShouldBe(WatchLivenessState.Working);
        verdict.Detail.ShouldContain("12");
    }

    [Fact]
    public void FreshHeartbeat_WithInterruptedPass_IsIdleAndNamesTheDebt()
    {
        // a pass the shutdown cut short is neither a wedge nor work in flight: the verdict has to say the
        // store is owed a reconciliation pass, not fall through to "phase not recognized"
        var pass = new PassPhaseStamp(
            HolderPid, PassPhaseStamp.InterruptedPhase, Now.AddMinutes(-3), Now.AddSeconds(-2), 5);

        var verdict = WatchLiveness.Classify(Heartbeat(secondsAgo: 5), pass, Now, Alive);

        verdict.State.ShouldBe(WatchLivenessState.Idle);
        verdict.Detail.ShouldContain("cut short");
        verdict.Detail.ShouldNotContain("not recognized");
    }

    [Fact]
    public void StaleHeartbeat_IsDead_RegardlessOfPassStamp()
    {
        var pass = new PassPhaseStamp(
            HolderPid, PassPhaseStamp.RunningPhase, Now.AddMinutes(-2), Now.AddMinutes(-2), 1);

        var verdict = WatchLiveness.Classify(Heartbeat(secondsAgo: 60), pass, Now, Alive);

        verdict.State.ShouldBe(WatchLivenessState.Dead);
    }

    [Fact]
    public void PassStampFromAnotherPid_IsIgnored()
    {
        var pass = new PassPhaseStamp(
            HolderPid + 1, PassPhaseStamp.StuckPhase, Now.AddMinutes(-9), Now.AddMinutes(-9), 1);

        var verdict = WatchLiveness.Classify(Heartbeat(secondsAgo: 5), pass, Now, Alive);

        verdict.State.ShouldBe(WatchLivenessState.Idle);
    }

    [Fact]
    public void ProcessGone_IsDead_EvenWithFreshBeat()
    {
        var verdict = WatchLiveness.Classify(Heartbeat(secondsAgo: 1), pass: null, Now, _ => false);

        verdict.State.ShouldBe(WatchLivenessState.Dead);
    }

    private static DaemonHeartbeat Heartbeat(int secondsAgo) => new(
        HolderPid,
        DaemonHeartbeat.DefaultKind,
        Now.AddHours(-2),
        Now.AddSeconds(-secondsAgo));

    private static bool Alive(int pid) => true;
}
