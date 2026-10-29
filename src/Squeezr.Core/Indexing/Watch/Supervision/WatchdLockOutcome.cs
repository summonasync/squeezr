namespace Squeezr.Core.Indexing.Watch.Supervision;

public readonly record struct WatchdLockOutcome(bool Acquired, int? OwnerPid);
