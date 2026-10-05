using Shouldly;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Ignore;

public class GitignoreMatcherTests
{
    [Fact]
    public void Parse_DirOnlyPattern_MatchesOnlyDirectories()
    {
        var pattern = GitignoreMatcher.Parse("out/", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.DirOnly.ShouldBeTrue();
        pattern.Matches("out", false).ShouldBeFalse();
        pattern.Matches("out", true).ShouldBeTrue();
        pattern.Matches("src/out", false).ShouldBeFalse();
        pattern.Matches("src/out", true).ShouldBeTrue();
    }

    [Fact]
    public void Parse_AnchoredDoubleStar_MatchesNestedPath()
    {
        var pattern = GitignoreMatcher.Parse("**/web/out/**", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Anchored.ShouldBeTrue();
        pattern.Matches("web/out/file.js", false).ShouldBeTrue();
        pattern.Matches("src/web/out/file.js", false).ShouldBeTrue();
        pattern.Matches("web/other/file.js", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_WildcardExtension_MatchesAnyDepth()
    {
        var pattern = GitignoreMatcher.Parse("*.js", IgnoreSource.Defaults, "defaults", ".");

        pattern.ShouldNotBeNull();
        pattern!.Anchored.ShouldBeFalse();
        pattern.Matches("file.js", false).ShouldBeTrue();
        pattern.Matches("src/file.js", false).ShouldBeTrue();
        pattern.Matches("src/deep/file.js", false).ShouldBeTrue();
        pattern.Matches("file.ts", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_Negate_ProducesNegatePattern()
    {
        var pattern = GitignoreMatcher.Parse("!keep.js", IgnoreSource.Squeezrignore, ".squeezrignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Negate.ShouldBeTrue();
        pattern.Matches("keep.js", false).ShouldBeTrue();
        pattern.Matches("other.js", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_LeadingSlash_AnchoredToRoot()
    {
        var pattern = GitignoreMatcher.Parse("/root-only.txt", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Anchored.ShouldBeTrue();
        pattern.Matches("root-only.txt", false).ShouldBeTrue();
        pattern.Matches("src/root-only.txt", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_DirDoubleStar_MatchesEverythingUnder()
    {
        var pattern = GitignoreMatcher.Parse("docs/**", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Anchored.ShouldBeTrue();
        pattern.Matches("docs/readme.md", false).ShouldBeTrue();
        pattern.Matches("docs/api/v1.md", false).ShouldBeTrue();
        pattern.Matches("src/docs/file.md", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_QuestionMark_MatchesSingleChar()
    {
        var pattern = GitignoreMatcher.Parse("a?c", IgnoreSource.Config, "config", ".");

        pattern.ShouldNotBeNull();
        pattern!.Anchored.ShouldBeFalse();
        pattern.Matches("abc", false).ShouldBeTrue();
        pattern.Matches("axc", false).ShouldBeTrue();
        pattern.Matches("ac", false).ShouldBeFalse();
        pattern.Matches("abbc", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_CharacterClass_IsSupportedAndMatchesMembers()
    {
        var pattern = GitignoreMatcher.Parse("[a-z].txt", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeFalse();
        pattern.Matches("a.txt", false).ShouldBeTrue();
        pattern.Matches("z.txt", false).ShouldBeTrue();
        pattern.Matches("file.txt", false).ShouldBeFalse();
        pattern.Matches(".txt", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_CharacterRange_CoversEndpointsOnly()
    {
        var pattern = GitignoreMatcher.Parse("[b-d]ack", IgnoreSource.Config, "config", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeFalse();
        pattern.Matches("back", false).ShouldBeTrue();
        pattern.Matches("cack", false).ShouldBeTrue();
        pattern.Matches("dack", false).ShouldBeTrue();
        pattern.Matches("ack", false).ShouldBeFalse();
        pattern.Matches("eack", false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("[!x]y")]
    [InlineData("[^x]y")]
    public void Parse_NegatedClass_AcceptsBangAndCaret(string raw)
    {
        var pattern = GitignoreMatcher.Parse(raw, IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeFalse();
        pattern.Matches("zy", false).ShouldBeTrue();
        pattern.Matches("xy", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_ClassWithLiteralBracketMembers_ReadsAsGitDoes()
    {
        var closing = GitignoreMatcher.Parse("[]a]z", IgnoreSource.Gitignore, ".gitignore", ".");
        var escaped = GitignoreMatcher.Parse("[\\[-\\]]b", IgnoreSource.Gitignore, ".gitignore", ".");

        closing.ShouldNotBeNull();
        closing!.Unsupported.ShouldBeFalse();
        closing.Matches("]z", false).ShouldBeTrue();
        closing.Matches("az", false).ShouldBeTrue();
        closing.Matches("bz", false).ShouldBeFalse();

        escaped.ShouldNotBeNull();
        escaped!.Unsupported.ShouldBeFalse();
        escaped.Matches("[b", false).ShouldBeTrue();
        escaped.Matches("]b", false).ShouldBeTrue();
        escaped.Matches("ab", false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("a\\*b", "a*b")]
    [InlineData("a\\?b", "a?b")]
    [InlineData("a\\[b", "a[b")]
    [InlineData("\\#hash", "#hash")]
    [InlineData("\\!bang", "!bang")]
    [InlineData("Generated\\ Files", "Generated Files")]
    public void Parse_BackslashEscape_TakesTheLiteralAndKeepsMagicOutside(string raw, string matchingPath)
    {
        var pattern = GitignoreMatcher.Parse(raw, IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeFalse();
        pattern.Negate.ShouldBeFalse();
        pattern.Matches(matchingPath, false).ShouldBeTrue(matchingPath);
    }

    [Theory]
    [InlineData("a\\*b", "axb")]
    [InlineData("a\\?b", "ab")]
    [InlineData("a\\[b", "ab")]
    [InlineData("Generated\\ Files", "GeneratedFiles")]
    [InlineData("Generated\\ Files", "Generated  Files")]
    public void Parse_BackslashEscape_DoesNotWildcardAnythingElse(string raw, string otherPath)
    {
        var pattern = GitignoreMatcher.Parse(raw, IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Matches(otherPath, false).ShouldBeFalse(otherPath);
    }

    [Fact]
    public void Parse_ClassAndEscape_LeaveFlagsAlone()
    {
        var negatedDir = GitignoreMatcher.Parse("!**/[Pp]ackages/build/", IgnoreSource.Gitignore, ".gitignore", ".");
        var escapedDir = GitignoreMatcher.Parse("Generated\\ Files/", IgnoreSource.Gitignore, ".gitignore", ".");
        var bangCache = GitignoreMatcher.Parse("!?*.[Cc]ache/", IgnoreSource.Gitignore, ".gitignore", ".");

        negatedDir.ShouldNotBeNull();
        negatedDir!.Negate.ShouldBeTrue();
        negatedDir.DirOnly.ShouldBeTrue();
        negatedDir.Anchored.ShouldBeTrue();
        negatedDir.Unsupported.ShouldBeFalse();

        escapedDir.ShouldNotBeNull();
        escapedDir!.DirOnly.ShouldBeTrue();
        escapedDir.Anchored.ShouldBeFalse();
        escapedDir.Unsupported.ShouldBeFalse();

        bangCache.ShouldNotBeNull();
        bangCache!.Negate.ShouldBeTrue();
        bangCache.DirOnly.ShouldBeTrue();
        bangCache.Matches("session.cache", isDirectory: true).ShouldBeTrue();
        bangCache.Matches("session.cache", isDirectory: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("[bracket")]
    [InlineData("x[[y")]
    [InlineData("[]")]
    [InlineData("[[:alpha:]]x")]
    [InlineData("[a[:digit:]b]Q")]
    [InlineData("[\\]")]
    [InlineData("a\\/b")]
    [InlineData("dangling\\")]
    public void Parse_ResidualShapes_AreMarkedUnsupported(string raw)
    {
        var pattern = GitignoreMatcher.Parse(raw, IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeTrue(raw);
        pattern.Matches(raw, false).ShouldBeFalse(raw);
    }

    [Fact]
    public void Parse_EscapedBackslashAsClassMember_IsExpressible()
    {
        var pattern = GitignoreMatcher.Parse("[\\\\]", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeFalse();
        pattern.CompiledPattern.ShouldBe("[\\\\]");
    }

    [Fact]
    public void Parse_EscapedTrailingSpace_IsResidualAndWarnsAboutTheLineAsWritten()
    {
        var pattern = GitignoreMatcher.Parse("foo\\ ", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeTrue();
        GitignoreMatcher.FormatWarning(pattern.Raw, ".gitignore")
            .ShouldBe("unsupported ignore pattern 'foo\\ ' in .gitignore; pattern skipped.");
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull()
    {
        GitignoreMatcher.Parse("", IgnoreSource.Gitignore, ".gitignore", ".").ShouldBeNull();
        GitignoreMatcher.Parse("   ", IgnoreSource.Gitignore, ".gitignore", ".").ShouldBeNull();
    }

    [Fact]
    public void Parse_CommentLine_ReturnsNull()
    {
        GitignoreMatcher.Parse("# this is a comment", IgnoreSource.Gitignore, ".gitignore", ".").ShouldBeNull();
    }

    [Fact]
    public void FormatWarning_ProducesExpectedFormat()
    {
        var warning = GitignoreMatcher.FormatWarning("[[:alpha:]].txt", ".gitignore");

        warning.ShouldBe("unsupported ignore pattern '[[:alpha:]].txt' in .gitignore; pattern skipped.");
    }
}
