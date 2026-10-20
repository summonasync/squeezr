using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Watch;

// The pass stamp is the hand-off between a watch engine that died and the one that replaces it: a record
// naming a writer that is gone means a pass never reported done, and the newcomer owes the store a
// reconciliation pass instead of waiting for an edit that already happened. A record naming a live writer
// belongs to that daemon and is not the newcomer's to erase.
public sealed class WorkspaceWatcherStampHandoffTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StartAsync_AbandonedStampOfAGoneWriter_ReconcilesTheEditMadeWhileStopped()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "a.cs", "class Alpha { string Term = \"beforestop\"; }");
        new WorkspaceIndexer().Index(workspace);

        var storeDir = SqueezrPaths.StoreDir(workspace);
        // an edit lands while no watch engine runs, and a stamp naming a writer that is gone stays behind
        WriteFile(workspace, "b.cs", "class Beta { string Term = \"catchupneedle\"; }");
        PassPhaseStampFile.Write(storeDir, new PassPhaseStamp(
            DeadPid, PassPhaseStamp.RunningPhase, DateTime.UtcNow, DateTime.UtcNow, 1));

        var recorder = new HandoffRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            await UntilAsync(() => recorder.PassesCompleted >= 1,
                () => $"faults=[{string.Join(" | ", recorder.Faults)}]");

            // consumption is proven positively: a pass ran with no event behind it and the edit made while
            // the engine was down is now searchable. Asserting the record is gone HERE would measure
            // "is a pass running right now" instead, because the live engine re-writes it at pass start
            new SearchEngine(workspace).Search(Query("catchupneedle")).Matches
                .ShouldContain(m => m.Path == "b.cs", "the abandoned batch must not wait for the next edit");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        // quiesced: with the run loop joined nothing can be mid-pass, so the hand-off having been consumed
        // is observable here and nowhere above
        PassPhaseStampFile.TryRead(storeDir).ShouldBeNull("the consumed hand-off must not outlive the engine");
    }

    [Fact]
    public async Task StopDuringARunningPass_HandsTheBatchBackAndLeavesTheStamp()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "f0007.cs", "class C0007 { string Term = \"needle0007\"; }");
        new WorkspaceIndexer().Index(workspace);

        var storeDir = SqueezrPaths.StoreDir(workspace);
        var target = Path.Combine(workspace, "f0007.cs");
        var recorder = new HandoffRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, passBudget: TimeSpan.FromMinutes(10));

        // the store lock is held by this test, so the pass provably cannot complete: it sits in the
        // WorkspaceLockedException retry loop, which is a guaranteed in-flight state at cancel time.
        // Betting on "a 600-file pass is probably still running" would be the same wall-clock gamble
        // this test exists to remove
        using (Squeezr.Core.Storage.IndexLock.Acquire(storeDir))
        {
            await watcher.StartAsync(workspace, cts.Token);
            try
            {
                watcher.OnEvent(target, pathMayBeGone: false);
                await UntilAsync(() => recorder.PassesStarted >= 1);
                await UntilAsync(() => PassPhaseStampFile.TryRead(storeDir) is not null);

                await cts.CancelAsync();
                await watcher.StopAsync();
            }
            finally
            {
                await StopWatcherAsync(watcher, cts);
            }
        }

        // quiesced and the lock is back: the batch the shutdown cut short came back to the pending set
        await Task.Delay(TimeSpan.FromSeconds(2));
        var handedBack = watcher.CollectDueBatch();
        handedBack.ShouldNotBeNull("the cut-short batch must come back to the pending set");
        handedBack.ShouldContain(target);

        // and what survives is exactly the named hand-off, never a running/stuck wedge record
        var survivor = PassPhaseStampFile.TryRead(storeDir);
        survivor.ShouldNotBeNull("a pass cut short must leave the hand-off behind");
        survivor.Phase.ShouldBe(PassPhaseStamp.InterruptedPhase);
        survivor.Pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task StartAsync_StampOfALiveForeignWriter_SurvivesTheNewcomer()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "a.cs", "class Alpha { string Term = \"liveholder\"; }");
        new WorkspaceIndexer().Index(workspace);

        var storeDir = SqueezrPaths.StoreDir(workspace);
        PassPhaseStampFile.Write(storeDir, new PassPhaseStamp(
            LiveForeignPid, PassPhaseStamp.StuckPhase, DateTime.UtcNow, DateTime.UtcNow, 4));

        var recorder = new HandoffRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            // erasing this record would hand every holder report a clean-looking store while that pass is
            // still stuck: the mask the whole wedge hunt was opened to remove
            var stamp = PassPhaseStampFile.TryRead(storeDir);
            stamp.ShouldNotBeNull("a live writer's wedge evidence must outlive a newcomer's start");
            stamp.Pid.ShouldBe(LiveForeignPid);
            stamp.Phase.ShouldBe(PassPhaseStamp.StuckPhase);
            recorder.PassesStarted.ShouldBe(0, "a live writer is not an unfinished-pass signal");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task StopCancelsAWalkingPass_TheLinkIsWhatLetsTheStopThrough()
    {
        var workspace = NewWorkspace();
        for (var i = 0; i < WalkItemCount; i++)
        {
            WriteFile(workspace, $"f{i:D3}.md", $"note {i} content");
        }
        new WorkspaceIndexer().Index(workspace);

        var storeDir = SqueezrPaths.StoreDir(workspace);
        var recorder = new HandoffRecorder();
        var progress = new WalkingProgress();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, progress, passBudget: TimeSpan.FromMinutes(10));
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            watcher.OnEvent(Path.Combine(workspace, "f000.md"), pathMayBeGone: false);
            progress.EnteredPass.Wait(WaitTimeout).ShouldBeTrue("the pass never reached the walk loop");

            // the pass is parked inside the walk loop on the test's own signal, so the stop lands while the
            // walk is provably mid-repo: no wall-clock bet, and 400 tiny files finishing before a cancel
            // could arrive (measured: PassesCompleted == 1 at load 6.5) cannot happen here
            await cts.CancelAsync();
            cts.Token.IsCancellationRequested.ShouldBeTrue("the stop token never reached its cancelled state");

            // counting starts AFTER the stop is a fact, not at the moment the test asked for it: CancelAsync
            // raises the token on a pooled worker, and the measured gap between the ask and the raise ate 144
            // walk items on a load-5 machine - that window is scheduling, not the link under test
            progress.CancelRequested = true;
            progress.ReleaseWalk.Set();
            await watcher.StopAsync();
        }
        finally
        {
            progress.ReleaseWalk.Set();
            await StopWatcherAsync(watcher, cts);
        }

        // released mid-walk with cancellation already raised: a linked budget throws at the walk's next
        // per-entry check, an unlinked one cannot hear the stop and walks the rest of the repo to a
        // completed pass - the discriminator is an item count and a pass count, never the clock
        progress.ItemsAfterCancel.ShouldBeLessThanOrEqualTo(
            WalkGraceItems, "the walk must stop at its next cancellation check, not walk the rest of the repo");
        recorder.PassesCompleted.ShouldBe(0, "a stopped pass never reports done");
        PassPhaseStampFile.TryRead(storeDir)!.Phase.ShouldBe(
            PassPhaseStamp.InterruptedPhase, "the cancelled pass must leave its hand-off behind");
    }

    private const int DeadPid = int.MaxValue;
    private const int LiveForeignPid = 1;
    private const int WalkItemCount = 400;
    private const int WalkGraceItems = 50;

    private static SearchQuery Query(string term) =>
        new(term, false, null, null, null, null, null, null);

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo(".", "demo");
        var config = ConfigFile.Load(workspace);
        // NoSemantic keeps the reconciliation pass cost deterministic: these tests pin who consumes the
        // hand-off record, not how fast a cold Roslyn session parses a repo under whatever else is running
        ConfigFile.Save(workspace, config with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 },
            Indexing = config.Indexing with { NoSemantic = true }
        });
        return workspace;
    }

    private static void WriteFile(string workspace, string name, string content) =>
        File.WriteAllText(Path.Combine(workspace, name), content);

    private static async Task UntilAsync(Func<bool> condition, Func<string>? failureDetail = null)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        condition().ShouldBeTrue($"condition not met within {WaitTimeout.TotalSeconds}s{failureDetail?.Invoke()}");
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

    // Counts walk iterations seen after the test asked for a stop. With the budget riding the stop token the
    // loop throws at its next cancellation check (a handful of items); without it the pass cannot hear the
    // stop and walks the rest of the repo, so the two cases are separated by orders of magnitude rather
    // than by a wall-clock bet: 400 items vs ~1
    private sealed class WalkingProgress : IIndexProgress
    {
        private static readonly TimeSpan WalkHold = TimeSpan.FromSeconds(20);
        private int _seen;

        public ManualResetEventSlim EnteredPass { get; } = new(false);

        public ManualResetEventSlim ReleaseWalk { get; } = new(false);

        public volatile bool CancelRequested;

        public int ItemsAfterCancel => Volatile.Read(ref _afterCancel);

        private int _afterCancel;

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
        {
        }

        public void RepoCompleted(string repoName)
        {
        }

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
        {
            if (Interlocked.Increment(ref _seen) == 1)
            {
                EnteredPass.Set();
                // held until the test raises its stop; bounded, so a missed release shows up as a completed
                // pass in the asserts rather than as a hung suite
                ReleaseWalk.Wait(WalkHold);
            }

            if (CancelRequested)
            {
                Interlocked.Increment(ref _afterCancel);
            }
        }
    }

    private sealed class HandoffRecorder : IWatchStatus
    {
        private readonly object _gate = new();
        private readonly List<string> _faults = [];
        private int _passesStarted;

        public int PassesStarted => Volatile.Read(ref _passesStarted);

        public int PassesCompleted => Volatile.Read(ref _passesCompleted);

        private int _passesCompleted;

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

        public void PassCompleted(IndexResult result) => Interlocked.Increment(ref _passesCompleted);

        public void RetryScheduled(TimeSpan delay)
        {
        }

        public void Faulted(string error)
        {
            lock (_gate)
            {
                _faults.Add(error);
            }
        }
    }
}
