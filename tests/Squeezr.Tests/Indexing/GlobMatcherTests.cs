using Shouldly;

namespace Squeezr.Tests.Indexing;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("**/node_modules/**", "node_modules/dep/index.js")]
    [InlineData("**/node_modules/**", "src/app/node_modules/dep/index.js")]
    [InlineData("**/bin/**", "bin/app.dll")]
    [InlineData("**/bin/**", "src/bin")]
    [InlineData("**/bin/**", "src/bin/Debug/app.dll")]
    [InlineData("**/obj/**", "a/b/obj/x")]
    [InlineData("**/*.min.js", "bundle.min.js")]
    [InlineData("**/*.min.js", "js/bundle.min.js")]
    [InlineData("*.cs", "Program.cs")]
    [InlineData("src/**", "src/a/b/c.cs")]
    [InlineData("?.txt", "a.txt")]
    public void IsMatch_MatchesExpectedPaths(string pattern, string path)
    {
        GlobMatcherIsMatch(pattern, path).ShouldBeTrue();
    }

    [Theory]
    [InlineData("**/node_modules/**", "src/node_modulesx/dep.js")]
    [InlineData("**/bin/**", "src/binary/data.bin")]
    [InlineData("**/*.min.js", "a.min.jsx")]
    [InlineData("*.cs", "src/Program.cs")]
    [InlineData("src/**", "src2/a.cs")]
    [InlineData("?.txt", "ab.txt")]
    public void IsMatch_RejectsNonMatchingPaths(string pattern, string path)
    {
        GlobMatcherIsMatch(pattern, path).ShouldBeFalse();
    }

    [Theory]
    [InlineData("**/bin/**", "Src/Bin/x.cs")]
    [InlineData("**/OBJ/**", "src/obj/Debug/x.dll")]
    [InlineData("src/**", "SRC/a.cs")]
    [InlineData("src\\**\\*.cs", "src/a.cs")]
    [InlineData("src\\**\\*.cs", "src/sub/b.cs")]
    [InlineData("*.cs", "PROGRAM.CS")]
    public void IsMatch_IgnoresCaseAndAcceptsBackslashSeparators(string pattern, string path)
    {
        GlobMatcherIsMatch(pattern, path).ShouldBeTrue();
    }

    private static bool GlobMatcherIsMatch(string pattern, string path) =>
        Squeezr.Core.Indexing.GlobMatcher.IsMatch(pattern, path);
}
