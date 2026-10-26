using Shouldly;
using Squeezr.Cli.RankEval;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.RankEval;

public class RankEvalScorerTests
{
    private static SearchMatch Match(string path, string repo = "Squeezr") =>
        new(repo, path, 1, 1.0, "snippet");

    private static GoldenQuery Query(params string[] expected) =>
        new("q", null, null, expected, "note");

    [Fact]
    public void SecondRankHit_MrrIsHalf_PrecisionCountsRecallFull()
    {
        var matches = new[] { Match("src/Other.cs"), Match("src/Target.cs") };
        var scored = RankEvalScorer.Score(Query("src/Target.cs"), matches);

        scored.Mrr.ShouldBe(0.5);
        scored.PrecisionAt5.ShouldBe(1.0 / 5);
        scored.RecallAt10.ShouldBe(1.0);
        scored.Ranks["src/Target.cs"].ShouldBe(2);
    }

    [Fact]
    public void ExpectedBeyondDepths_ScoresZeroButRankIsReported()
    {
        var matches = Enumerable.Range(1, 20).Select(i => Match($"src/File{i:00}.cs")).ToList();
        matches.Add(Match("src/Target.cs"));
        var scored = RankEvalScorer.Score(Query("src/Target.cs"), matches);

        scored.Mrr.ShouldBe(0.0);
        scored.PrecisionAt5.ShouldBe(0.0);
        scored.RecallAt10.ShouldBe(0.0);
        scored.Ranks["src/Target.cs"].ShouldBe(21);
    }

    [Fact]
    public void MissingExpected_RanksZero_AllMetricsZero()
    {
        var scored = RankEvalScorer.Score(Query("src/Target.cs"), [Match("src/Other.cs")]);

        scored.Mrr.ShouldBe(0.0);
        scored.PrecisionAt5.ShouldBe(0.0);
        scored.RecallAt10.ShouldBe(0.0);
        scored.Ranks["src/Target.cs"].ShouldBe(0);
    }

    [Fact]
    public void RepoScopedEntry_DoesNotMatchOtherRepo()
    {
        var matches = new[] { Match("src/Target.cs", repo: "Other") };
        var scoped = new GoldenQuery("q", "Squeezr", null, ["src/Target.cs"], "note");

        RankEvalScorer.Score(scoped, matches).Ranks["src/Target.cs"].ShouldBe(0);
    }

    [Fact]
    public void MultipleExpected_RecallIsFractionFound()
    {
        var matches = new[] { Match("src/A.cs"), Match("src/B.cs"), Match("src/C.cs") };
        var scored = RankEvalScorer.Score(Query("src/A.cs", "src/missing.cs", "src/C.cs"), matches);

        scored.RecallAt10.ShouldBe(2.0 / 3);
        scored.Mrr.ShouldBe(1.0);
        scored.PrecisionAt5.ShouldBe(2.0 / 5);
    }

    [Fact]
    public void Load_RejectsEntryWithoutExpectedPaths()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "bad-set.json");
        File.WriteAllText(path, """[{ "query": "q", "expected": [] }, { "query": "ok", "expected": ["src/A.cs"] }]""");

        var ex = Should.Throw<InvalidDataException>(() => GoldenQuery.Load(path));
        ex.Message.ShouldContain("#1");
    }

    [Fact]
    public void Load_RejectsEmptySet()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "empty-set.json");
        File.WriteAllText(path, "[]");

        Should.Throw<InvalidDataException>(() => GoldenQuery.Load(path));
    }
}
