using Shouldly;
using Tg = Squeezr.Core.Indexing.Trigram;

namespace Squeezr.Tests.Indexing.Trigram;

public class TrigramExtractorTests
{
    [Fact]
    public void ComputeHash_IsCaseSensitive()
    {
        Tg.TrigramHasher.ComputeHash('a', 'b', 'c')
            .ShouldNotBe(Tg.TrigramHasher.ComputeHash('A', 'b', 'c'));
    }

    [Fact]
    public void ComputeHash_MatchesPolynomialFormula()
    {
        Tg.TrigramHasher.ComputeHash('a', 'b', 'c').ShouldBe(96_354u);
    }

    [Fact]
    public void ExtractPostings_IndexesEveryWindowWithPosition()
    {
        var postings = Tg.TrigramExtractor.ExtractPostings("foobar");

        postings.Count.ShouldBe(4);
        postings[Tg.TrigramHasher.ComputeHash('f', 'o', 'o')].ShouldBe([0]);
        postings[Tg.TrigramHasher.ComputeHash('o', 'o', 'b')].ShouldBe([1]);
        postings[Tg.TrigramHasher.ComputeHash('o', 'b', 'a')].ShouldBe([2]);
        postings[Tg.TrigramHasher.ComputeHash('b', 'a', 'r')].ShouldBe([3]);
    }

    [Fact]
    public void ExtractPostings_GroupsRepeatedTrigram()
    {
        var postings = Tg.TrigramExtractor.ExtractPostings("aaaaaa");

        postings.Count.ShouldBe(1);
        postings[Tg.TrigramHasher.ComputeHash('a', 'a', 'a')].ShouldBe([0, 1, 2, 3]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void ExtractPostings_TextShorterThanThreeChars_YieldsNothing(string text)
    {
        Tg.TrigramExtractor.ExtractPostings(text).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractPostings_HandlesMultibyteCharacters()
    {
        var text = "фубар";
        var postings = Tg.TrigramExtractor.ExtractPostings(text);

        postings.Count.ShouldBe(3);
        postings[Tg.TrigramHasher.ComputeHash('ф', 'у', 'б')].ShouldBe([0]);
        postings[Tg.TrigramHasher.ComputeHash('у', 'б', 'а')].ShouldBe([1]);
        postings[Tg.TrigramHasher.ComputeHash('б', 'а', 'р')].ShouldBe([2]);
    }
}
