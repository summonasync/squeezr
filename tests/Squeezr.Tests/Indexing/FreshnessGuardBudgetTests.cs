using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// The budget is pinned with a spy that counts re-reads and a clock only a re-read moves: the caps the guard
// compares against are the shipped ones, the files it refuses are real, and no test waits out an index pass.
public class FreshnessGuardBudgetTests
{
    [Fact]
    public void CandidatesBeyondTheFileBudget_StayStaleAsUnrefreshed_AndNeverReachTheIndexer()
    {
        var total = RefreshBudget.MaxFilesReRead + 1;
        var workspace = StaleWorkspace(total);
        var spy = new SpyReindex();

        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Starts.ShouldBe(RefreshBudget.MaxFilesReRead, "one call starts this many re-reads and no more");
        result.Refreshed.ShouldBe(Candidates(total - 1).Select(Key));
        result.Unrefreshed.ShouldHaveSingleItem()
            .ShouldBe(Key(("demo", $"unit{RefreshBudget.MaxFilesReRead}.cs")), StringComparer.Ordinal);
        result.Stale.ShouldHaveSingleItem().ShouldBe(result.Unrefreshed[0], StringComparer.Ordinal,
            "a re-read that succeeded leaves the stale list; the one the budget refused does not — the answer " +
            "loses no file either way");
        result.Stale.ShouldContain(result.Unrefreshed[0], StringComparer.Ordinal,
            "Unrefreshed is a named part of Stale, never a second list the caller has to subtract");
        spy.ReRead.ShouldNotContain(result.Unrefreshed[0]);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    public void CallWhoseTimeBudgetIsSpent_StopsStartingReReads_AndNamesHowManyItLeft(int total)
    {
        var workspace = StaleWorkspace(total);
        var costPerFile = RefreshBudget.MaxWallClockMillis / 2 + 1;
        var spy = new SpyReindex(costMillisPerFile: costPerFile);

        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Starts.ShouldBe(2,
            $"two re-reads at {costPerFile} ms pass the {RefreshBudget.MaxWallClockMillis} ms bound between the " +
            "second and the third candidate — the wall clock is what ended this call, not the file cap");
        spy.Starts.ShouldBeLessThan(RefreshBudget.MaxFilesReRead,
            "the time bound has to bite before the count bound for this to be the time bound's own test");
        result.Unrefreshed.Count.ShouldBe(total - 2,
            "and every candidate behind the bound is named as left, so the remainder is counted, not guessed");
        result.Refreshed.Count.ShouldBe(2);
        result.Stale.Count.ShouldBe(total - 2,
            "everything the clock refused is still named stale, and nothing else is");
    }

    // the floor is what keeps a slow call honest: the bound is a rule about starting another re-read, not a
    // way to hand back a call that healed nothing while naming every stale row as though it could not help
    [Fact]
    public void WallBoundAlreadySpent_BeforeTheFirstCandidate_StillReReadsOneFile()
    {
        var total = RefreshBudget.MaxFilesReRead + 2;
        var workspace = StaleWorkspace(total);
        var spy = new SpyReindex(elapsedFromStart: TimeSpan.FromMilliseconds(RefreshBudget.MaxWallClockMillis * 40));

        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Starts.ShouldBe(1,
            "the first stale candidate is never refused for time, so a call that found a stale file heals one");
        result.Refreshed.ShouldHaveSingleItem();
        result.Unrefreshed.Count.ShouldBe(total - 1);
        result.Stale.Count.ShouldBe(total - 1);
    }

    [Fact]
    public void FreshCandidatePastTheBudget_IsStillChecked_AndStaysOutOfBothTallies()
    {
        var workspace = StaleWorkspace(RefreshBudget.MaxFilesReRead);
        File.WriteAllText(In(workspace, "src/extra.cs"), Class("Extra"));
        var spy = new SpyReindex();

        List<(string Repo, string Path)> ordered =
            [.. Candidates(RefreshBudget.MaxFilesReRead), ("demo", "keep.cs"), ("demo", "extra.cs")];
        var result = CheckWithBudget(workspace, spy, ordered);

        result.Refreshed.Count.ShouldBe(RefreshBudget.MaxFilesReRead);
        result.Stale.ShouldNotContain("demo/keep.cs", StringComparer.Ordinal,
            "the budget refuses re-reads, not examination — a fresh file past the bound still reads fresh");
        result.Unrefreshed.ShouldHaveSingleItem().ShouldBe("demo/extra.cs", StringComparer.Ordinal,
            "and the stale one behind it is refused as itself");
        spy.Starts.ShouldBe(RefreshBudget.MaxFilesReRead);
    }

    [Fact]
    public void ReReadThatFailed_IsNotCountedAsABudgetRefusal()
    {
        var total = RefreshBudget.MaxFilesReRead + 1;
        var workspace = StaleWorkspace(total);
        var spy = new SpyReindex(failures: 1);

        var result = CheckWithBudget(workspace, spy, Candidates(total));

        result.Refreshed.Count.ShouldBe(RefreshBudget.MaxFilesReRead - 1,
            "the first attempt threw, so one fewer row came back");
        result.Stale.ShouldContain("demo/unit0.cs", StringComparer.Ordinal,
            "a failed re-read is stale because its row still describes other content");
        result.Unrefreshed.ShouldHaveSingleItem().ShouldBe(
            $"demo/unit{RefreshBudget.MaxFilesReRead}.cs", StringComparer.Ordinal,
            "examined-and-refused and left-no-room are two statements and never share one tally");
        result.Stale.Count.ShouldBe(2, "one thrown attempt and one refusal, and the two are told apart above");
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

        var spy = new SpyReindex();
        var result = CheckWithBudget(workspace, spy, Candidates(total));

        spy.Starts.ShouldBe(0, "a call that cannot read the workspace re-reads nothing either");
        result.Stale.Count.ShouldBe(total, "the collapsed call still reaches every candidate as stale");
        result.Unrefreshed.ShouldBeEmpty(
            "a workspace this call could not open is not a budget, and attributing its whole list to one is " +
            "the reading this pin exists to make impossible");
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
            "re-read has already started. A cap spelled as a literal in the loop is the magic number this task " +
            "promised not to leave in the code");
    }

    private static FreshnessResult CheckWithBudget(
        string workspace,
        SpyReindex spy,
        IReadOnlyList<(string Repo, string Path)> candidates) =>
        FreshnessGuard.CheckAndRefresh(workspace, candidates, null, spy.Run, () => spy.Elapsed);

    private static List<(string Repo, string Path)> Candidates(int count) =>
        [.. Enumerable.Range(0, count).Select(i => ("demo", $"unit{i}.cs"))];

    private static string Key((string Repo, string Path) candidate) => $"{candidate.Repo}/{candidate.Path}";

    private static string Class(string name) =>
        $"class {name} {{ string Value = \"budgetneedle\"; }}\n";

    private static string In(string workspace, string relative) =>
        Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    // files whose rows are behind their content by a real edit: every candidate is one the guard wants to
    // re-read, and the spy is the only thing deciding whether the call may start it. keep.cs joins the same
    // index and is never touched, so it is the fresh position in the list.
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

    private sealed class SpyReindex
    {
        private readonly int _costMillisPerFile;
        private readonly int _failures;
        private int _starts;

        public SpyReindex(
            int costMillisPerFile = 0,
            int failures = 0,
            TimeSpan? elapsedFromStart = null)
        {
            _costMillisPerFile = costMillisPerFile;
            _failures = failures;
            Elapsed = elapsedFromStart ?? TimeSpan.Zero;
        }

        public int Starts => _starts;

        public TimeSpan Elapsed { get; private set; }

        public List<string> ReRead { get; } = [];

        public void Run(string workspaceRoot, string repo, string path, Action<string>? onStaleTakeover)
        {
            _starts++;
            Elapsed += TimeSpan.FromMilliseconds(_costMillisPerFile);
            if (_starts <= _failures)
            {
                throw new IOException("spy re-read refused on purpose");
            }

            ReRead.Add($"{repo}/{path}");
        }
    }
}
