using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Watch;

// The bounded pass (055/T5): a pass that crosses its budget is treated as wedged, the watchdog paints
// the stuck stamp before the cooperative abort can land, the lock is never stranded by an aborted pass,
// and the budget escalates across consecutive overruns so a slow repo converges instead of livelocking.
public sealed class WorkspaceWatcherPassBudgetTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task OverrunPass_IsFaultedWithBudgetReason_AbortedAndLockReleased()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "class Alpha { string Term = \"slowprobe\"; }");
        new WorkspaceIndexer().Index(workspace);

        var recorder = new BudgetRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, passBudget: TimeSpan.Zero);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            watcher.OnEvent(Path.Combine(workspace, "a.cs"), pathMayBeGone: false);

            await UntilAsync(() => recorder.Faults.Any(f => f.Contains("budget", StringComparison.Ordinal)),
                () => $"faults=[{string.Join(" | ", recorder.Faults)}]");

            recorder.Faults.ShouldContain(f => f.Contains("abort", StringComparison.Ordinal));
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        // after the run loop is joined, none of the aborted passes may have left index.lock behind
        using (var probe = Squeezr.Core.Storage.IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            probe.ShouldNotBeNull();
        }

        // the invariant this test actually owns: a joined run loop may not leave WEDGE evidence behind.
        // A re-queued pass cut short by the shutdown is allowed to leave one named record — the explicit
        // hand-off, and only that one — because the next start consumes it as unfinished work. Asserting
        // "no record at all" here would race that deliberate hand-off instead of pinning anything.
        // Scope of the pin, stated plainly: this measures the KIND of a stamp that happens to be there,
        // never that a stamp exists — existence is owned by
        // StopDuringARunningPass_HandsTheBatchBackAndLeavesTheStamp, so reading the sentence above as
        // "no stamp may outlive a joined run loop" would claim more coverage than this test provides.
        var survivor = PassPhaseStampFile.TryRead(SqueezrPaths.StoreDir(workspace));
        if (survivor is not null)
        {
            survivor.Phase.ShouldBe(
                PassPhaseStamp.InterruptedPhase, "only a hand-off may outlive a joined run loop");
            survivor.Pid.ShouldBe(Environment.ProcessId);
        }
    }

    [Fact]
    public async Task WedgeFault_SaysWhatTheAbortCannotDo_AndHandsOverTheRecovery()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "class Alpha { string Term = \"wedgefault\"; }");
        new WorkspaceIndexer().Index(workspace);

        var recorder = new BudgetRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, passBudget: TimeSpan.Zero);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            watcher.OnEvent(Path.Combine(workspace, "a.cs"), pathMayBeGone: false);

            await UntilAsync(() => WedgeFault(recorder) is not null);

            var wedge = WedgeFault(recorder)!;
            // the abort is cooperative: a pass parked inside a syscall keeps the store lock no matter what
            // this timer says, so the message must name the recovery that actually works and never promise
            // that the lock went back with the pass
            wedge.ShouldContain("syscall");
            wedge.ShouldContain("restart");
            wedge.ShouldNotContain("released with the pass");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task ConsecutiveOverruns_EscalateTheArmedBudget_AndACompletedPassSnapsItBack()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "notes.md"), "escalation probe text");
        File.WriteAllText(Path.Combine(workspace, "more.md"), "escalation probe text two");
        new WorkspaceIndexer().Index(workspace);

        var recorder = new BudgetRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, passBudget: EscalationFloor);
        recorder.ArmedProbe = () => watcher.ArmedPassBudget;
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            watcher.OnEvent(Path.Combine(workspace, "notes.md"), pathMayBeGone: false);

            // the floor is below any real pass cost, so the first pass must overrun — that is what makes the
            // later completion a measured reset rather than a budget that never moved. The probe re-arms
            // wider than the debounce window (firing inside it pushes the due time and no pass ever starts,
            // measured earlier in this task) and waits against a starvation-sized deadline: under full-suite
            // load the run loop's continuations get starved, measured as zero faults inside 20s at load 25.7
            await UntilAsync(
                () =>
                {
                    watcher.OnEvent(Path.Combine(workspace, "notes.md"), pathMayBeGone: false);
                    return recorder.BudgetOverrunsSeen >= 1;
                },
                StarvationDeadline,
                () => $"started={recorder.PassesStarted} faults=[{string.Join(" | ", recorder.Faults)}]",
                DebounceReArmMs);
            recorder.BudgetOverrunsSeen.ShouldBeGreaterThan(0, "the probe measured no overrun at all");

            // the fault is what the watchdog publishes when the budget expires; the ladder is armed later,
            // where the pass notices the abort. Waiting on the fault and asserting the armed budget in the
            // same breath measures the gap between two threads instead of the escalation
            await UntilAsync(
                () => watcher.ArmedPassBudget > EscalationFloor,
                StarvationDeadline,
                () => $"armed={watcher.ArmedPassBudget} started={recorder.PassesStarted} " +
                      $"faults=[{string.Join(" | ", recorder.Faults)}]");

            await UntilAsync(
                () => recorder.PassesCompleted >= 1,
                () => $"faults=[{string.Join(" | ", recorder.Faults)}] armed={watcher.ArmedPassBudget}");
            recorder.ArmedAtLastCompletion.ShouldBe(EscalationFloor, "a pass that fit resets the ladder");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task HealthyPass_WritesAndClearsRunningStamp_BetweenPassesLockIsFree()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "class Alpha { string Term = \"phaseprobe\"; }");
        new WorkspaceIndexer().Index(workspace);

        var recorder = new BudgetRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, passBudget: TimeSpan.FromMinutes(10));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            watcher.OnEvent(Path.Combine(workspace, "a.cs"), pathMayBeGone: false);

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            var stamp = PassPhaseStampFile.TryRead(SqueezrPaths.StoreDir(workspace));
            if (stamp is not null)
            {
                // the pass is still finishing its bookkeeping: a live stamp may only name the running phase
                stamp.Phase.ShouldBe(PassPhaseStamp.RunningPhase);
            }
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        PassPhaseStampFile.TryRead(SqueezrPaths.StoreDir(workspace)).ShouldBeNull();
        using var probe = Squeezr.Core.Storage.IndexLock.Acquire(SqueezrPaths.StoreDir(workspace));
        probe.ShouldNotBeNull();
    }

    private static readonly TimeSpan EscalationFloor = TimeSpan.FromMilliseconds(2);

    // sized to outlast a thread-pool starvation spike from the parallel CLI collections in a full suite run,
    // not to make the escalation itself look slow: the mechanism answers inside a second on an idle box
    private static readonly TimeSpan StarvationDeadline = TimeSpan.FromSeconds(90);

    private const int DebounceReArmMs = 2000;

    private static string? WedgeFault(BudgetRecorder recorder) =>
        recorder.Faults.FirstOrDefault(f => f.Contains("treated as wedged", StringComparison.Ordinal));

    private static async Task UntilAsync(Func<bool> condition, Func<string>? failureDetail = null) =>
        await UntilAsync(condition, WaitTimeout, failureDetail);

    private static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string>? failureDetail = null,
        int pollMs = 100)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(pollMs);
        }

        condition().ShouldBeTrue(
            $"condition not met within {timeout.TotalSeconds}s{failureDetail?.Invoke()}");
    }

    private static async Task StopWatcherAsync(WorkspaceWatcher watcher, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        var stop = watcher.StopAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!stop.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        stop.IsCompleted.ShouldBeTrue("watcher did not stop within 5s");
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo(".", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        return workspace;
    }

    private sealed class BudgetRecorder : IWatchStatus
    {
        private readonly object _gate = new();
        private readonly List<string> _faults = [];
        private int _passesStarted;
        private int _passesCompleted;
        private int _budgetOverrunsSeen;

        public int PassesStarted => Volatile.Read(ref _passesStarted);

        public int PassesCompleted => Volatile.Read(ref _passesCompleted);

        public int BudgetOverrunsSeen => Volatile.Read(ref _budgetOverrunsSeen);

        public Func<TimeSpan>? ArmedProbe { get; set; }

        public TimeSpan? ArmedAtLastCompletion { get; private set; }

        public IReadOnlyList<string> Faults
        {
            get
            {
                lock (_gate)
                {
                    return _faults.ToArray();
                }
            }
        }

        public void PassStarted(int changedPaths) => Interlocked.Increment(ref _passesStarted);

        public void PassCompleted(IndexResult result)
        {
            ArmedAtLastCompletion = ArmedProbe?.Invoke();
            Interlocked.Increment(ref _passesCompleted);
        }

        public void RetryScheduled(TimeSpan delay)
        {
        }

        public void Faulted(string error)
        {
            if (error.Contains("treated as wedged", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _budgetOverrunsSeen);
            }

            lock (_gate)
            {
                _faults.Add(error);
            }
        }
    }
}
