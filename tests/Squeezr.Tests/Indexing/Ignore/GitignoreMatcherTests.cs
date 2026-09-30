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
    public void Parse_UnsupportedCharacterClass_MarkedUnsupported()
    {
        var pattern = GitignoreMatcher.Parse("[a-z].txt", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeTrue();
        pattern.Matches("a.txt", false).ShouldBeFalse();
        pattern.Matches("file.txt", false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_UnsupportedBackslash_MarkedUnsupported()
    {
        var pattern = GitignoreMatcher.Parse("file\\ name.txt", IgnoreSource.Gitignore, ".gitignore", ".");

        pattern.ShouldNotBeNull();
        pattern!.Unsupported.ShouldBeTrue();
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
        var warning = GitignoreMatcher.FormatWarning("[a-z].txt", ".gitignore");

        warning.ShouldBe("unsupported ignore pattern '[a-z].txt' in .gitignore; pattern skipped.");
    }
}
