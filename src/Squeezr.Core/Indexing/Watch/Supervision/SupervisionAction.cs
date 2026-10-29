namespace Squeezr.Core.Indexing.Watch.Supervision;

public enum SupervisionAction
{
    Started,
    LeftRunning,
    DriftRestarted,
    StoppedByDesire,
    LeftStopped,
    SkippedLiveOwner,
    BlockedByCrashLoop,
    StartFailed,
    SkippedRoot,
    ProbeFailed,
}
