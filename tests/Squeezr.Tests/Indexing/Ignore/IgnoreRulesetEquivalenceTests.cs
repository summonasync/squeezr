using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Ignore;

// The read path calls IgnoreRuleset.Decision once per tree entry, so the hot loop was rewritten to match on
// pre-split path segments. This is the guard: the segment matcher must decide exactly like the nested
// join-and-rematch algorithm it replaced, on every pattern shape and every path in the corpus.
// The reference implementation predates character classes and backslash escapes, so the fixture keeps its
// residual shapes ([[:alpha:]], a dangling backslash) out of the compared set — the new dialect is pinned
// against git itself in GitignoreOracleTests, where guessing is not an option.
public sealed class IgnoreRulesetEquivalenceTests : IDisposable
{
    private static readonly string[] CorpusPaths =
    [
        "a",
        "a.log",
        "keep.log",
        "a/",
        "/a.log",
        "src//deep/x.tmp",
        "src/deep//x.js",
        "src/Calc.cs",
        "src/keep.cs",
        "src/deep/deeper/x.cs",
        "build/out.js",
        "build/nested/deep.js",
        "nested/build/out.js",
        "x/y/gen/file.js",
        "docs/api/v1.md",
        "root-only.txt",
        "src/root-only.txt",
        "src/a/b/vendor/lib.js",
        "node_modules/pkg/index.js",
        "web/out/index.js",
        "dist/app.js",
        "a.min.js",
        "nested/deep.js",
        "web/x.js",
        "Web/x.js",
        "WEB/deep/x.JS",
        "src/Mixed/Case.cs",
        "tmp/a.log",
        "cfg/note.md",
        "notes.md",
        "src/secret/key.pem",
        "src/deep/a.map",
        "src/deep/a.js",
        "İstanbul/x.log",
        "Ünload/x.tmp",
        "back\\slash/x.log",
        "a/b/c/d/e/f/g/h.log",
        "*.log",
        "[bracket].tmp",
        "",
        "."
    ];

    private readonly string _root = TestDirs.NewTempDir();

    [Fact]
    public void Decision_SegmentMatcher_MatchesLegacyNestedLoop()
    {
        BuildFixture();
        var ruleset = IgnoreRuleset.For(_root, FixtureConfig());
        var patterns = ruleset.Patterns;
        patterns.Count.ShouldBeGreaterThan(12);

        var corpus = CorpusPaths.Concat(EnumerateTreePaths()).ToArray();
        var checkedCombinations = 0;

        foreach (var path in corpus)
        {
            foreach (var isDirectory in new[] { false, true })
            {
                var expected = LegacyDecide(patterns, path, isDirectory);
                var actual = ruleset.Decision(path, isDirectory);

                actual.Excluded.ShouldBe(expected.Excluded, $"exclusion changed for '{path}' (dir={isDirectory})");
                actual.Source.ShouldBe(expected.Source, $"source changed for '{path}' (dir={isDirectory})");
                actual.Pattern.ShouldBe(expected.Pattern, $"pattern changed for '{path}' (dir={isDirectory})");
                checkedCombinations++;
            }
        }

        checkedCombinations.ShouldBe(corpus.Length * 2);
    }

    [Fact]
    public void PatternMatches_SegmentMatcher_MatchesLegacyGlob()
    {
        BuildFixture();
        var ruleset = IgnoreRuleset.For(_root, FixtureConfig());

        foreach (var pattern in ruleset.Patterns)
        {
            foreach (var path in CorpusPaths.Concat(EnumerateTreePaths()))
            {
                pattern.Matches(path, false).ShouldBe(LegacyPatternMatches(pattern, path, false), $"file '{path}' vs '{pattern.Raw}'");
                pattern.Matches(path, true).ShouldBe(LegacyPatternMatches(pattern, path, true), $"dir '{path}' vs '{pattern.Raw}'");
            }
        }
    }

    [Theory]
    [InlineData("web/out/", "web/out/x.js", false)]
    [InlineData("web/out/", "web/out", true)]
    [InlineData("/root-only.txt", "root-only.txt", false)]
    [InlineData("/root-only.txt", "src/root-only.txt", false)]
    [InlineData("**/gen/", "x/y/gen/a.js", false)]
    [InlineData("docs/**", "docs/api/v1.md", false)]
    [InlineData("*.log", "keep.log", false)]
    [InlineData("src/**/*.cs", "src/a/b/c.cs", false)]
    [InlineData("Web/", "web/x.js", false)]
    [InlineData("Ü/", "ü/x.js", false)]
    [InlineData("a/b", "a\\b", false)]
    [InlineData("nested/", "src/nested/deep/a.cs", true)]
    public void SinglePatternDecisions_AgreeWithLegacy(string rawPattern, string path, bool isDirectory)
    {
        Write(".gitignore", rawPattern + "\n");
        var ruleset = IgnoreRuleset.For(_root, FixtureConfig());

        var expected = LegacyDecide(ruleset.Patterns, path, isDirectory);
        var actual = ruleset.Decision(path, isDirectory);

        actual.Excluded.ShouldBe(expected.Excluded, $"'{rawPattern}' vs '{path}' (dir={isDirectory})");
        actual.Source.ShouldBe(expected.Source, $"'{rawPattern}' vs '{path}' (dir={isDirectory})");
        actual.Pattern.ShouldBe(expected.Pattern, $"'{rawPattern}' vs '{path}' (dir={isDirectory})");
    }

    // The corpus diff above agrees with legacy everywhere, so these three assertions pin the branches a
    // future refactor is most likely to break silently: re-include under an excluded parent (must NOT
    // re-include a file, must reset on the directory itself) and source attribution of the built-in defaults.
    [Fact]
    public void Decision_NegateUnderExcludedParentAndDefaultsAttribution()
    {
        BuildFixture();
        var ruleset = IgnoreRuleset.For(_root, FixtureConfig());

        var file = ruleset.Decision("nested/deep.js", isDirectory: false);
        file.Excluded.ShouldBe(true, "!nested/deep.js must not re-include a file under excluded nested/");
        file.Pattern.ShouldBe("nested/");
        file.Source.ShouldBe(IgnoreSource.Gitignore);

        ruleset.Decision("nested/deep.js", isDirectory: true).Excluded.ShouldBe(
            false, "the negate matches the directory itself and clears the parent exclusion");

        var underDefaults = ruleset.Decision("node_modules/pkg/index.js", isDirectory: false);
        underDefaults.Excluded.ShouldBe(true);
        underDefaults.Source.ShouldBe(IgnoreSource.Defaults);
        ruleset.Decision("a.min.js", isDirectory: false).Source.ShouldBe(IgnoreSource.Defaults);
        ruleset.Decision("dist/app.js", isDirectory: false).Source.ShouldBe(IgnoreSource.Defaults);

        // Last-match-wins across layers: the built-in **/web/out/** is shadowed by the fixture's own Web/,
        // so the source that wins the attribution is the later layer, not the earlier one.
        ruleset.Decision("web/out/index.js", isDirectory: false).Source.ShouldBe(IgnoreSource.Gitignore);
    }

    private static SqueezrConfig FixtureConfig() =>
        SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = ["cfg/*.md", "*.bak"],
                RespectGitignore = true
            }
        };

    private void BuildFixture()
    {
        Write(".gitignore", """
            *.log
            !keep.log
            build/
            **/gen/
            /root-only.txt
            docs/**
            src/**/vendor/
            nested/
            !nested/deep.js
            Web/
            *.tmp
            [[:alpha:]]
            dangling\
            İ*/

            """);
        Write("src/.gitignore", """
            *.cs
            !keep.cs
            secret/

            """);
        Write("src/deep/.gitignore", """
            !**/x.js
            *.map

            """);
        Write("src/Mixed/.gitignore", """
            Case.cs

            """);
        Write("build/.gitignore", """
            out.js

            """);
        Write(".squeezrignore", """
            tmp/

            """);
        foreach (var file in new[]
                 {
                     "keep.log", "a.log", "root-only.txt", "notes.md", "cfg/note.md",
                     "src/Calc.cs", "src/keep.cs", "src/secret/key.pem",
                     "src/deep/a.map", "src/deep/x.js", "src/Mixed/Case.cs",
                     "src/a/b/vendor/lib.js", "build/out.js", "build/nested/deep.js",
                     "web/x.js", "Web/x.js", "WEB/deep/x.JS", "x/y/gen/file.js",
                     "docs/api/v1.md", "tmp/a.log", "nested/build/out.js", "nested/deep.js",
                     "node_modules/pkg/index.js", "web/out/index.js", "a.min.js"
                 })
        {
            Write(file, "x");
        }
    }

    private IEnumerable<string> EnumerateTreePaths()
    {
        var paths = new List<string>();
        foreach (var entry in Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, entry).Replace('\\', '/');
            var segments = relative.Split('/');
            for (var i = 1; i <= segments.Length; i++)
                paths.Add(string.Join('/', segments.Take(i)));
        }

        return paths.Distinct(StringComparer.Ordinal);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose() => Directory.Delete(_root, true);

    // ---- reference implementation, verbatim from the pre-optimization sources ----

    private static IgnoreDecision LegacyDecide(
        IReadOnlyList<IgnorePattern> patterns, string relativePath, bool isDirectory)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var segments = normalizedPath.Split('/');

        IgnoreDecision? lastMatch = null;
        bool anyParentExcluded = false;

        foreach (var pattern in patterns)
        {
            for (var i = 1; i <= segments.Length; i++)
            {
                var isLast = i == segments.Length;
                var isDir = !isLast || (isLast && isDirectory);
                var path = string.Join('/', segments.Take(i));

                if (!LegacyPatternMatches(pattern, path, isDir, respectUnsupportedFlag: false))
                    continue;

                if (pattern.Negate)
                {
                    if (isLast && !anyParentExcluded)
                    {
                        lastMatch = new IgnoreDecision { Excluded = false, Source = pattern.Source, Pattern = pattern.Raw };
                    }
                    else if (isDir)
                    {
                        anyParentExcluded = false;
                        lastMatch = new IgnoreDecision { Excluded = false, Source = pattern.Source, Pattern = pattern.Raw };
                    }
                }
                else
                {
                    lastMatch = new IgnoreDecision { Excluded = true, Source = pattern.Source, Pattern = pattern.Raw };

                    if (isDir && !isLast && pattern.DirOnly)
                        anyParentExcluded = true;
                }
            }
        }

        return lastMatch ?? new IgnoreDecision { Excluded = false, Source = null, Pattern = null };
    }

    private static bool LegacyPatternMatches(IgnorePattern pattern, string path, bool isDirectory) =>
        LegacyPatternMatches(pattern, path, isDirectory, respectUnsupportedFlag: true);

    private static bool LegacyPatternMatches(
        IgnorePattern pattern, string path, bool isDirectory, bool respectUnsupportedFlag)
    {
        if (respectUnsupportedFlag && pattern.Unsupported)
            return false;

        if (pattern.DirOnly && !isDirectory)
            return false;

        var normalizedPath = path.Replace('\\', '/');

        if (!string.IsNullOrEmpty(pattern.BaseDir) && pattern.BaseDir != ".")
        {
            var baseDirNormalized = pattern.BaseDir.Replace('\\', '/').TrimEnd('/');
            if (!normalizedPath.StartsWith(baseDirNormalized + "/", StringComparison.Ordinal) &&
                normalizedPath != baseDirNormalized)
                return false;

            string relativePath;
            if (normalizedPath == baseDirNormalized)
            {
                relativePath = baseDirNormalized;
            }
            else if (normalizedPath.Length == baseDirNormalized.Length + 1)
            {
                relativePath = string.Empty;
            }
            else
            {
                relativePath = normalizedPath.Substring(baseDirNormalized.Length + 1);
            }

            var matchPath = pattern.Anchored ? relativePath : LegacyFileName(relativePath);
            return LegacyIsMatch(pattern.CompiledPattern, matchPath);
        }

        var matchPath2 = pattern.Anchored ? normalizedPath : LegacyFileName(normalizedPath);
        return LegacyIsMatch(pattern.CompiledPattern, matchPath2);
    }

    private static string LegacyFileName(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
    }

    private static bool LegacyIsMatch(string pattern, string path) =>
        LegacyMatchSegments(pattern.Replace('\\', '/').ToLowerInvariant().Split('/'), 0, path.ToLowerInvariant().Split('/'), 0);

    private static bool LegacyMatchSegments(string[] pattern, int patternIndex, string[] segments, int segmentIndex)
    {
        if (patternIndex == pattern.Length)
            return segmentIndex == segments.Length;

        if (pattern[patternIndex] == "**")
        {
            for (var skip = segmentIndex; skip <= segments.Length; skip++)
            {
                if (LegacyMatchSegments(pattern, patternIndex + 1, segments, skip))
                    return true;
            }

            return false;
        }

        if (segmentIndex == segments.Length)
            return false;

        return LegacyMatchSegment(pattern[patternIndex], segments[segmentIndex])
               && LegacyMatchSegments(pattern, patternIndex + 1, segments, segmentIndex + 1);
    }

    private static bool LegacyMatchSegment(string pattern, string segment)
    {
        var p = 0;
        var s = 0;
        var starPattern = -1;
        var starSegment = -1;

        while (s < segment.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == segment[s]))
            {
                p++;
                s++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starSegment = s;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                s = ++starSegment;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }
}
