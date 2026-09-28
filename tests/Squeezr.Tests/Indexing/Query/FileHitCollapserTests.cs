using Shouldly;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class FileHitCollapserTests
{
    [Fact]
    public void SpansOfOneFile_CollapseIntoTheBestScoredHit()
    {
        var ranked = new List<SearchMatch>
        {
            Hit("docs/guide.md", 40, 0.9),
            Hit("docs/guide.md", 12, 0.4),
            Hit("docs/guide.md", 77, 0.2)
        };

        var hit = FileHitCollapser.Collapse(ranked).ShouldHaveSingleItem();

        hit.Line.ShouldBe(40);
        hit.Score.ShouldBe(0.9);
        hit.AlsoMatched.ShouldBe(2);
    }

    [Fact]
    public void DistinctFiles_PassThroughUnchanged()
    {
        var ranked = new List<SearchMatch>
        {
            Hit("a.cs", 1, 0.9),
            Hit("b.cs", 5, 0.5),
            Hit("c.cs", 9, 0.1)
        };

        var collapsed = FileHitCollapser.Collapse(ranked);

        collapsed.ShouldBe(ranked);
        collapsed.ShouldAllBe(m => m.AlsoMatched == 0);
    }

    [Fact]
    public void InterleavedFiles_KeepFirstAppearanceOrder()
    {
        var ranked = new List<SearchMatch>
        {
            Hit("b.cs", 1, 0.9),
            Hit("a.cs", 1, 0.8),
            Hit("b.cs", 20, 0.7),
            Hit("a.cs", 30, 0.6)
        };

        var collapsed = FileHitCollapser.Collapse(ranked);

        collapsed.Select(m => m.Path).ShouldBe(["b.cs", "a.cs"]);
        collapsed.Select(m => m.AlsoMatched).ShouldBe([1, 1]);
        collapsed[0].Line.ShouldBe(1);
        collapsed[1].Line.ShouldBe(1);
    }

    [Fact]
    public void EqualScores_LowestLineWins_WhateverTheInputOrder()
    {
        var descending = FileHitCollapser.Collapse([Hit("a.cs", 30, 0.5), Hit("a.cs", 12, 0.5)]);
        var ascending = FileHitCollapser.Collapse([Hit("a.cs", 12, 0.5), Hit("a.cs", 30, 0.5)]);

        descending.ShouldHaveSingleItem().Line.ShouldBe(12);
        ascending.ShouldHaveSingleItem().Line.ShouldBe(12);
    }

    [Fact]
    public void SamePathInTwoRepos_StaysTwoHits()
    {
        var ranked = new List<SearchMatch>
        {
            new("demo", "src/a.cs", 1, 0.9, "snippet"),
            new("other", "src/a.cs", 4, 0.8, "snippet")
        };

        var collapsed = FileHitCollapser.Collapse(ranked);

        collapsed.Count.ShouldBe(2);
        collapsed.ShouldAllBe(m => m.AlsoMatched == 0);
    }

    [Fact]
    public void WinningSpanCarriesItsOwnSourceAndSnippet()
    {
        var ranked = new List<SearchMatch>
        {
            new SearchMatch("demo", "a.cs", 1, 0.3, "first span") { Source = "semantic" },
            new SearchMatch("demo", "a.cs", 8, 0.9, "second span") { Source = "both" }
        };

        var hit = FileHitCollapser.Collapse(ranked).ShouldHaveSingleItem();

        hit.Source.ShouldBe("both");
        hit.Snippet.ShouldBe("second span");
        hit.Line.ShouldBe(8);
        hit.AlsoMatched.ShouldBe(1);
    }

    [Fact]
    public void EmptyRanking_CollapsesToEmpty() =>
        FileHitCollapser.Collapse([]).ShouldBeEmpty();

    private static SearchMatch Hit(string path, int line, double score) =>
        new("demo", path, line, score, "snippet");
}
