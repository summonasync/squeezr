using Shouldly;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class SemanticSnippetTests
{
    [Fact]
    public void Build_SmallChunk_ReturnsTrimmedTextVerbatim()
    {
        SemanticSnippet.Build("  line one\nline two  ", "anything").ShouldBe("line one\nline two");
    }

    [Fact]
    public void Build_ChunkAtTheCap_ReturnsItVerbatim()
    {
        var chunk = new string('x', SemanticSnippet.MaxChars);

        SemanticSnippet.Build(chunk, "anything").ShouldBe(chunk);
    }

    [Fact]
    public void Build_NoTokenMatch_PreviewsFromTheTop()
    {
        var chunk = $"# Heading\n\n{new string('a', 220)}\n\ntail marker";

        var snippet = SemanticSnippet.Build(chunk, "zznotaword");

        snippet.ShouldBe("# Heading " + new string('a', 190) + "…");
        snippet.Length.ShouldBe(SemanticSnippet.MaxChars + 1);
    }

    [Fact]
    public void Build_MatchedLineLeads_WhenQueryTokensHitIt()
    {
        var chunk = $"""
            # Heading

            {new string('x', 190)}

            the planning slug carries slugmark for this fixture line
            {new string('y', 220)}
            """;

        var snippet = SemanticSnippet.Build(chunk, "planning slugmark");

        snippet.ShouldStartWith("the planning slug carries slugmark for this fixture line");
        snippet.ShouldNotContain("Heading");
        snippet.Length.ShouldBeLessThanOrEqualTo(SemanticSnippet.MaxChars + 1);
        snippet.ShouldEndWith("…");
    }

    [Fact]
    public void Build_LongSingleLine_CapsAtMarker()
    {
        var snippet = SemanticSnippet.Build(new string('x', 500), "anything");

        snippet.Length.ShouldBe(SemanticSnippet.MaxChars + 1);
        snippet.ShouldEndWith("…");
    }

    [Fact]
    public void Build_BlankChunk_StaysEmpty()
    {
        SemanticSnippet.Build("   \n\t\n  ", "anything").ShouldBeEmpty();
    }
}
