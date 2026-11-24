using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// The budget is pinned with a spy that counts the one batch a call may start and records what the guard
// announced into it: the caps the guard compares against are the shipped ones, the files it refuses are
// real, and no test waits out an index pass.
public class FreshnessGuardBudgetTests
{
    [Fact]
    public void CandidatesBeyondTheFileBudget_StayStaleAsUnrefreshed_AndNeverReachTheIndexer()
    {
        var total = RefreshBudget.MaxFilesReRead + 1;
        var workspace = StaleWorkspace(total);
        var spy = new SpyBatch();

        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Batches.ShouldBe(1, "one call starts one batch and no more");
        spy.Announced.ShouldBe(Candidates(RefreshBudget.MaxFilesReRead).Select(Key), customMessage:
            "the batch carries this many candidates and no more — the cap is its capacity");
        result.Refreshed.ShouldBe(Candidates(total - 1).Select(Key));
        result.Unrefreshed.ShouldHaveSingleItem()
            .ShouldBe(Key(("demo", $"unit{RefreshBudget.MaxFilesReRead}.cs")), StringComparer.Ordinal);
        result.Stale.ShouldHaveSingleItem().ShouldBe(result.Unrefreshed[0], StringComparer.Ordinal,
            "a candidate the batch healed leaves the stale list; the one the budget refused does not — the answer " +
            "loses no file either way");
        result.Stale.ShouldContain(result.Unrefreshed[0], StringComparer.Ordinal,
            "Unrefreshed is a named part of Stale, never a second list the caller has to subtract");
        spy.Announced.ShouldNotContain(result.Unrefreshed[0], StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    public void CallWhoseTimeBudgetIsSpent_StopsTakingSlots_AndNamesHowManyItLeft(int total)
    {
        var workspace = StaleWorkspace(total);
        var costPerPoll = RefreshBudget.MaxWallClockMillis / 2 + 1;
        var polls = 0;
        var spy = new SpyBatch();

        var result = CheckWithBudget(workspace, spy, Candidates(total),
            elapsed: () => TimeSpan.FromMilliseconds(costPerPoll * ++polls));

        spy.Announced.Count.ShouldBe(2,
            $"two slots at {costPerPoll} ms a poll pass the {RefreshBudget.MaxWallClockMillis} ms bound " +
            "between the second and the third candidate — the wall clock is what ended this call, not the batch cap");
        spy.Batches.ShouldBe(1);
        spy.Announced.Count.ShouldBeLessThan(RefreshBudget.MaxFilesReRead,
            "the time bound has to bite before the count bound for this to be the time bound's own test");
        result.Unrefreshed.Count.ShouldBe(total - 2,
            "and every candidate behind the bound is named as left, so the remainder is counted, not guessed");
        result.Refreshed.Count.ShouldBe(2);
        result.Stale.Count.ShouldBe(total - 2,
            "everything the clock refused is still named stale, and nothing else is");
    }

    // the floor is what keeps a slow call honest: the bound is a rule about taking another slot, not a
    // way to hand back a call that healed nothing while naming every stale row as though it could not help
    [Fact]
    public void WallBoundAlreadySpent_BeforeTheFirstCandidate_StillAnnouncesOneCandidate()
    {
        var total = RefreshBudget.MaxFilesReRead + 2;
        var workspace = StaleWorkspace(total);
        var spy = new SpyBatch();

        var result = CheckWithBudget(workspace, spy, Candidates(total),
            elapsed: () => TimeSpan.FromMilliseconds(RefreshBudget.MaxWallClockMillis * 40));

        spy.Announced.Count.ShouldBe(1,
            "the first stale candidate is never refused for time, so a call that found a stale file heals one");
        spy.Batches.ShouldBe(1);
        result.Refreshed.ShouldHaveSingleItem();
        result.Unrefreshed.Count.ShouldBe(total - 1);
        result.Stale.Count.ShouldBe(total - 1);
    }

    [Fact]
    public void FreshCandidatePastTheBudget_IsStillChecked_AndStaysOutOfBothTallies()
    {
        var workspace = StaleWorkspace(RefreshBudget.MaxFilesReRead);
        File.WriteAllText(In(workspace, "src/extra.cs"), Class("Extra"));
        var spy = new SpyBatch();

        List<(string Repo, string Path)> ordered =
            [.. Candidates(RefreshBudget.MaxFilesReRead), ("demo", "keep.cs"), ("demo", "extra.cs")];
        var result = CheckWithBudget(workspace, spy, ordered);

        result.Refreshed.Count.ShouldBe(RefreshBudget.MaxFilesReRead);
        result.Stale.ShouldNotContain("demo/keep.cs", StringComparer.Ordinal,
            "the budget refuses slots, not examination — a fresh file past the bound still reads fresh");
        result.Unrefreshed.ShouldHaveSingleItem().ShouldBe("demo/extra.cs", StringComparer.Ordinal,
            "and the stale one behind it is refused as itself");
        spy.Announced.Count.ShouldBe(RefreshBudget.MaxFilesReRead);
        spy.Batches.ShouldBe(1);
    }

    // the batch is one route now: when it throws, no announced candidate can carry a healed verdict, and
    // the tally has to say so without turning the throw into a budget refusal it did not earn
    [Fact]
    public void BatchThatThrew_IsNotCountedAsABudgetRefusal()
    {
        var total = RefreshBudget.MaxFilesReRead + 1;
        var workspace = StaleWorkspace(total);
        var spy = new SpyBatch(failures: 1);

        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Batches.ShouldBe(1);
        result.Refreshed.ShouldBeEmpty(
            "the batch answered nothing it can vouch for, so no announced candidate is refreshed");
        result.Stale.Count.ShouldBe(total,
            "every announced candidate keeps its stale verdict and the refused one keeps its own");
        result.Stale.ShouldContain("demo/unit0.cs", StringComparer.Ordinal,
            "a thrown batch is stale because its rows still describe other content");
        result.Unrefreshed.ShouldHaveSingleItem().ShouldBe(
            $"demo/unit{RefreshBudget.MaxFilesReRead}.cs", StringComparer.Ordinal,
            "examined-and-refused and left-no-room are two statements and never share one tally");
    }

    [Theory]
    [InlineData("store")]
    [InlineData("manifest")]
    public void CollapseShapes_LeavesNoBudgetTally_AtAll(string unexamined)
    {
        var total = RefreshBudget.MaxFilesReRead + 3;
        var workspace = StaleWorkspace(total);
        if (unexamined == "store")
        {
            File.Delete(SqueezrPaths.MetaDbPath(workspace));
        }
        else
        {
            File.Delete(SqueezrPaths.ManifestPath(workspace));
        }

        var spy = new SpyBatch();
        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Batches.ShouldBe(0, "a call that cannot read the workspace starts no batch either");
        result.Stale.Count.ShouldBe(total, "the collapsed call still reaches every candidate as stale");
        result.Unrefreshed.ShouldBeEmpty(
            "a workspace this call could not open is not a budget, and attributing its whole list to one is " +
            "the reading this pin exists to make impossible");
    }

    // cancellation is observed at the one deterministic point the budget refuses at, so a call cancelled
    // before its first acceptance tells the same story as a workspace it could not open: nothing spent,
    // nothing refused, everything stale — the collapse shape, never a budget verdict it did not earn
    [Fact]
    public void CallCancelledBeforeItsFirstBatch_CollapsesToStale_AndLeavesNoBudgetTally()
    {
        var total = RefreshBudget.MaxFilesReRead + 3;
        var workspace = StaleWorkspace(total);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var spy = new SpyBatch();

        var result = CheckWithBudget(workspace, spy, Candidates(total), cts.Token);

        spy.Batches.ShouldBe(0, "a call cancelled before its first acceptance never reaches the indexer");
        result.Refreshed.ShouldBeEmpty();
        result.Stale.Count.ShouldBe(total, "the call stopped before it could vouch for any candidate");
        result.Unrefreshed.ShouldBeEmpty(
            "nothing reached the budget to be refused by it: an empty Unrefreshed is what a call that healed " +
            "nothing says, and cancellation before the first acceptance is that call");
    }

    // the batch under way when the token fires is not interrupted: it completes and every candidate it
    // carried stays healed, and the tail the cap refused before the token fired stays refused —
    // boundary-only cancellation across one boundary per call, with both shapes still readable in the answer
    [Fact]
    public void CallCancelledInsideItsBatch_KeepsTheBatchHealed_AndTheRefusedTailRefused()
    {
        var total = RefreshBudget.MaxFilesReRead + 2;
        var workspace = StaleWorkspace(total);
        using var cts = new CancellationTokenSource();
        var spy = new SpyBatch(onStart: () => cts.Cancel());

        var result = CheckWithBudget(workspace, spy, Candidates(total), cts.Token);

        spy.Batches.ShouldBe(1, "cancellation is boundary-only: the batch it fired inside finishes");
        result.Refreshed.ShouldBe(Candidates(RefreshBudget.MaxFilesReRead).Select(Key), customMessage:
            "the batch answered every candidate it carried, and the post-batch tally still maps them all");
        result.Unrefreshed.ShouldBe(
            [
                Key(("demo", $"unit{RefreshBudget.MaxFilesReRead}.cs")),
                Key(("demo", $"unit{RefreshBudget.MaxFilesReRead + 1}.cs")),
            ],
            StringComparer.Ordinal);
        result.Stale.ShouldBe(result.Unrefreshed, customMessage:
            "the refused tail is stale; nothing else is — the healed batch and the refused tail stay two shapes");
    }

    [Fact]
    public void BothCapsAreDeclaredInOnePlace_AndTheGuardSpellsNoNumberForThem()
    {
        var budget = Read("src/Squeezr.Core/Indexing/RefreshBudget.cs");
        CountMatches(budget, "public const int").ShouldBe(2,
            "the two caps and nothing else — a third budget constant would hide a third limit");
        budget.ShouldContain("MaxFilesReRead");
        budget.ShouldContain("MaxWallClockMillis");

        var guard = Read("src/Squeezr.Core/Indexing/FreshnessGuard.cs");
        var loop = CandidateLoopBody(guard);
        loop.ShouldContain("RefreshBudget.MaxFilesReRead");
        loop.ShouldContain("RefreshBudget.MaxWallClockMillis");
        NumbersIn(loop).ShouldBe(["0"],
            "the candidate loop reads both caps by name and owns one digit only: the zero that asks whether a " +
            "slot has been taken yet. A cap spelled as a literal in the loop is the magic number this task " +
            "promised not to leave in the code");
    }

    // the wrapper is the only place where the budget meets a real stopwatch and the real batch route, and
    // the spy-driven tests above cannot see either: swap the clock for a constant or wire the wrapper to
    // anything but IndexScoped and every other test in this class still passes. No test exercises
    // onStaleTakeover through the public overload, which is why the route half is pinned here.
    [Fact]
    public void PublicWrapper_WiresTheRealClock_AndForwardsTheBatchRoute()
    {
        var wrapper = PublicWrapperBody(Read("src/Squeezr.Core/Indexing/FreshnessGuard.cs"));
        wrapper.ShouldContain("Stopwatch.StartNew()");
        wrapper.ShouldContain("() => clock.Elapsed");
        wrapper.ShouldNotContain("TimeSpan.Zero");
        wrapper.ShouldContain("IndexScoped(",
            customMessage: "the wrapper must wire the real batch route — the seam is only as real as what production hands it");
    }

    private static FreshnessResult CheckWithBudget(
        string workspace,
        SpyBatch spy,
        IReadOnlyList<(string Repo, string Path)> candidates,
        CancellationToken cancellationToken = default,
        Func<TimeSpan>? elapsed = null) =>
        FreshnessGuard.CheckAndRefresh(
            workspace, candidates, null, spy.Run, elapsed ?? (() => TimeSpan.Zero), cancellationToken);

    private static List<(string Repo, string Path)> Candidates(int count) =>
        [.. Enumerable.Range(0, count).Select(i => ("demo", $"unit{i}.cs"))];

    private static string Key((string Repo, string Path) candidate) => $"{candidate.Repo}/{candidate.Path}";

    private static string Class(string name) =>
        $"class {name} {{ string Value = \"budgetneedle\"; }}\n";

    private static string In(string workspace, string relative) =>
        Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    // files whose rows are behind their content by a real edit: every candidate is one the guard wants to
    // re-read, and the spy is the only thing deciding whether the call may announce it. keep.cs joins the
    // same index and is never touched, so it is the fresh position in the list.
    private static string StaleWorkspace(int count)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(In(workspace, "src"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("src", "demo");
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(In(workspace, $"src/unit{i}.cs"), Class($"Unit{i}"));
        }

        File.WriteAllText(In(workspace, "src/keep.cs"), Class("Keep"));
        new WorkspaceIndexer().Index(workspace);

        for (var i = 0; i < count; i++)
        {
            File.AppendAllText(In(workspace, $"src/unit{i}.cs"), "// past the stamp\n");
        }

        return workspace;
    }

    private static string Read(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Squeezr.slnx not found above the test assembly"),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int CountMatches(string source, string needle) =>
        Regex.Matches(source, Regex.Escape(needle)).Count;

    private static List<string> NumbersIn(string source) =>
        [.. Regex.Matches(source, @"(?<![\w.])\d+[\d_]*(?![\w.])")
            .Select(m => m.Value.Replace("_", string.Empty))
            .Distinct()];

    private static string PublicWrapperBody(string source)
    {
        const string open = "public static FreshnessResult CheckAndRefresh(";
        const string close = "internal static FreshnessResult CheckAndRefresh(";
        var from = source.IndexOf(open, StringComparison.Ordinal);
        from.ShouldBeGreaterThan(-1, $"the guard no longer has the '{open}' wrapper this pin reads");
        var to = source.IndexOf(close, StringComparison.Ordinal);
        to.ShouldBeGreaterThan(from,
            $"the seam overload ('{close}') must stay below the public wrapper — this pin reads the block between them");
        return source[from..to];
    }

    private static string CandidateLoopBody(string source)
    {
        const string header = "foreach (var candidate in candidates)";
        var headerAt = source.IndexOf(header, StringComparison.Ordinal);
        headerAt.ShouldBeGreaterThan(-1, $"the guard no longer has the '{header}' loop this pin reads");

        var open = source.IndexOf('{', headerAt + header.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("unbalanced braces in the candidate loop");
    }

    private sealed class SpyBatch
    {
        private readonly int _failures;
        private readonly Action? _onStart;
        private readonly List<string> _announced = [];
        private int _batches;

        public SpyBatch(int failures = 0, Action? onStart = null)
        {
            _failures = failures;
            _onStart = onStart;
        }

        public int Batches => _batches;

        public IReadOnlyList<string> Announced => _announced;

        public WorkspaceIndexer.ScopedIndexResult Run(
            string workspaceRoot,
            IReadOnlyList<(string Repo, string Path, string AbsolutePath)> batch,
            Action<string>? onStaleTakeover)
        {
            _batches++;
            _onStart?.Invoke();
            _announced.AddRange(batch.Select(candidate => $"{candidate.Repo}/{candidate.Path}"));
            if (_batches <= _failures)
            {
                throw new IOException("spy batch refused on purpose");
            }

            return new WorkspaceIndexer.ScopedIndexResult(batch.Count, 0, []);
        }
    }
}
