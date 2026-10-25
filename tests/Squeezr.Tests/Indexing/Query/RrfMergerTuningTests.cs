using Shouldly;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class RrfMergerTuningTests
{
    private static SearchMatch Hit(string path) => new("demo", path, 1, 0.0, "snippet");

    private static List<(string Key, SearchMatch Match)> Arm(params string[] paths) =>
        paths.Select((p, i) => ($"k{i}:{p}", Hit(p))).ToList();

    [Fact]
    public void DefaultTuning_MatchesHardcodedBehavior()
    {
        var exact = Arm("a.cs", "b.cs");
        var semantic = Arm("c.cs");

        var plain = RrfMerger.Merge(exact, semantic);
        var empty = RrfMerger.Merge(exact, semantic, new FusionTuning());

        plain.Select(m => (m.Path, m.Score, m.Source)).ShouldBe(empty.Select(m => (m.Path, m.Score, m.Source)));
    }

    [Fact]
    public void CustomK_ChangesAbsoluteScoresNotSingleArmOrder()
    {
        var merged = RrfMerger.Merge(Arm("a.cs", "b.cs"), [], new FusionTuning(K: 1));

        merged[0].Score.ShouldBe(1.0 / 2, 1e-9);
        merged[1].Score.ShouldBe(1.0 / 3, 1e-9);
    }

    [Fact]
    public void ZeroWeight_DropsTheArmToZeroScoreButKeepsTheEntry()
    {
        var merged = RrfMerger.Merge(Arm("a.cs"), Arm("s.cs"), new FusionTuning(ExactWeight: 0.0));

        merged[0].Path.ShouldBe("s.cs");
        merged[0].Score.ShouldBe(1.0 / 61, 1e-9);
        merged.Last().Path.ShouldBe("a.cs");
        merged.Last().Score.ShouldBe(0.0);
    }

    [Fact]
    public void WeightedArm_AmplifiesItsContribution()
    {
        var merged = RrfMerger.Merge([], Arm("s.cs"), new FusionTuning(SemanticWeight: 2.0));

        merged[0].Score.ShouldBe(2.0 / 61, 1e-9);
    }

    [Fact]
    public void BothArmsWeightedZero_LeaveZeroScoresButKeepEntries()
    {
        var merged = RrfMerger.Merge(Arm("a.cs"), Arm("s.cs"), new FusionTuning(ExactWeight: 0.0, SemanticWeight: 0.0));

        merged.Select(m => m.Score).ShouldBe([0.0, 0.0]);
        merged.Select(m => m.Source).ShouldBe(["exact", "semantic"]);
    }
}
