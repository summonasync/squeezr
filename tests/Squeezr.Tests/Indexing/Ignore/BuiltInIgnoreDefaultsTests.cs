using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Ignore;

public class BuiltInIgnoreDefaultsTests
{
    [Theory]
    [InlineData("**/web/out/**", "web/out/app.js", true)]
    [InlineData("**/web/out/**", "web/out/nested/deep.js", true)]
    [InlineData("**/.qwen/tmp/**", ".qwen/tmp/session.txt", true)]
    [InlineData("**/.qwen/tmp/**", ".qwen/tmp/loop/args.json", true)]
    [InlineData("**/coverage/**", "coverage/lcov.info", true)]
    [InlineData("**/coverage/**", "tests/coverage/report.html", true)]
    [InlineData("**/.turbo/**", ".turbo/turbo.log", true)]
    [InlineData("**/.turbo/**", "packages/web/.turbo/build.log", true)]
    [InlineData("**/.pytest_cache/**", ".pytest_cache/v/cache/step", true)]
    [InlineData("**/.pytest_cache/**", "tests/.pytest_cache/README.md", true)]
    [InlineData("**/.gradle/**", ".gradle/8.5/checksums/checksums.lock", true)]
    [InlineData("**/.gradle/**", "subproject/.gradle/buildOutputCleanup/cache.properties", true)]
    [InlineData("**/TestResults/**", "TestResults/results.trx", true)]
    [InlineData("**/TestResults/**", "tests/TestResults/coverage.xml", true)]
    public void NewDefaultPattern_ExcludesExpectedPath(string pattern, string path, bool excluded)
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = [pattern],
                RespectGitignore = false
            }
        };

        var ruleset = IgnoreRuleset.For(root, config);
        var decision = ruleset.Decision(path, false);

        decision.Excluded.ShouldBe(excluded);
    }

    [Fact]
    public void Defaults_DoNotExcludeArbitraryOutDirectory()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("out/file.js", false).Excluded.ShouldBeFalse();
        ruleset.Decision("src/out/module.js", false).Excluded.ShouldBeFalse();
        ruleset.Decision("deep/nested/out/file.cs", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void WebOutDefault_ExcludesOnlyUnderWeb()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("web/out/app.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/out/nested/deep.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("src/out/module.js", false).Excluded.ShouldBeFalse();
        ruleset.Decision("out/file.js", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void GitignoreOutSlash_ExcludesArbitraryOut_WithGitignoreSource()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "out/\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        var decision = ruleset.Decision("src/out/module.js", false);
        decision.Excluded.ShouldBeTrue();
        decision.Source.ShouldBe(IgnoreSource.Gitignore);

        var rootOut = ruleset.Decision("out/file.js", false);
        rootOut.Excluded.ShouldBeTrue();
        rootOut.Source.ShouldBe(IgnoreSource.Gitignore);
    }

    [Fact]
    public void QwenTmpDefault_ExcludesSessionJunk()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision(".qwen/tmp/qwen-skill-args-loop.txt", false).Excluded.ShouldBeTrue();
        ruleset.Decision(".qwen/tmp/session.log", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void QwenTmpDefault_DoesNotMatchQwenConfigFiles()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision(".qwen/QWEN.md", false).Excluded.ShouldBeFalse();
        ruleset.Decision(".qwen/settings.json", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void CoverageDefault_DoesNotMatchSourceCode()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("src/CoverageService.cs", false).Excluded.ShouldBeFalse();
        ruleset.Decision("coverage/report/index.html", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void TestResultsDefault_DoesNotMatchTestSource()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("tests/TestResultsHelper.cs", false).Excluded.ShouldBeFalse();
        ruleset.Decision("TestResults/2026-09-28/results.trx", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void AllNewDefaults_SourceIsDefaults_WhenConfigIgnoreEmpty()
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = [],
                RespectGitignore = false
            }
        };
        var ruleset = IgnoreRuleset.For(root, config);

        var samples = new (string Path, bool IsDir)[]
        {
            ("web/out/app.js", false),
            (".qwen/tmp/session.txt", false),
            ("coverage/lcov.info", false),
            (".turbo/turbo.log", false),
            (".pytest_cache/v/cache/step", false),
            (".gradle/8.5/gc.properties", false),
            ("TestResults/results.trx", false)
        };

        foreach (var (path, isDir) in samples)
        {
            var decision = ruleset.Decision(path, isDir);
            decision.Excluded.ShouldBeTrue($"path '{path}' should be excluded by defaults");
            decision.Source.ShouldBe(IgnoreSource.Defaults, $"source for '{path}' should be Defaults");
        }
    }

    [Fact]
    public void MissingDefaults_LegacyConfig_AppendsNewPatternsNoDuplicates()
    {
        var legacyIgnore = new[] { "**/node_modules/**", "**/bin/**", "**/obj/**" };
        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;

        var missing = defaults
            .Where(glob => !legacyIgnore.Contains(glob, StringComparer.Ordinal))
            .ToArray();

        missing.ShouldContain("**/web/out/**");
        missing.ShouldContain("**/.qwen/tmp/**");
        missing.ShouldContain("**/coverage/**");
        missing.ShouldContain("**/TestResults/**");
        missing.ShouldNotContain("**/node_modules/**");
        missing.ShouldNotContain("**/bin/**");
        missing.ShouldNotContain("**/obj/**");

        var merged = legacyIgnore.Concat(missing).ToArray();
        merged.Length.ShouldBe(legacyIgnore.Length + missing.Length);
        merged.Distinct(StringComparer.Ordinal).Count().ShouldBe(merged.Length);
    }

    [Fact]
    public void MissingDefaults_FreshConfig_NothingMissing()
    {
        var freshIgnore = SqueezrConfig.CreateDefault().Indexing.Ignore;
        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;

        var missing = defaults
            .Where(glob => !freshIgnore.Contains(glob, StringComparer.Ordinal))
            .ToArray();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void MissingDefaults_UserPatterns_PreservedInOrder()
    {
        var userIgnore = new[] { "**/node_modules/**", "**/my-secret/**" };
        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;

        var missing = defaults
            .Where(glob => !userIgnore.Contains(glob, StringComparer.Ordinal))
            .ToArray();

        var merged = userIgnore.Concat(missing).ToArray();

        var mySecretIdx = Array.IndexOf(merged, "**/my-secret/**");
        var webOutIdx = Array.IndexOf(merged, "**/web/out/**");
        mySecretIdx.ShouldBeLessThan(webOutIdx);

        merged.ShouldContain("**/my-secret/**");
        merged.ShouldContain("**/web/out/**");
        merged.ShouldContain("**/.qwen/tmp/**");
    }

    [Fact]
    public void NewDefaults_DoNotContainBroadOutMask()
    {
        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;

        defaults.ShouldNotContain("**/out/**");
        defaults.ShouldContain("**/web/out/**");
        defaults.ShouldContain("**/.qwen/tmp/**");
        defaults.ShouldContain("**/coverage/**");
        defaults.ShouldContain("**/.turbo/**");
        defaults.ShouldContain("**/.pytest_cache/**");
        defaults.ShouldContain("**/.gradle/**");
        defaults.ShouldContain("**/TestResults/**");

        defaults.Count(d => d is "**/web/out/**" or "**/.qwen/tmp/**"
            or "**/coverage/**" or "**/.turbo/**" or "**/.pytest_cache/**"
            or "**/.gradle/**" or "**/TestResults/**").ShouldBe(7);
    }
}
