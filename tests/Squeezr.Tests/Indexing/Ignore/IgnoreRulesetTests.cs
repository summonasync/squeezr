using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing.Ignore;

public class IgnoreRulesetTests
{
    [Fact]
    public void Cascade_NestedGitignore_ExcludesFileInSubdir()
    {
        var root = TestDirs.NewTempDir();
        var webDir = Path.Combine(root, "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, ".gitignore"), "out/\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("web/out/x.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/src/app.js", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void Cascade_RootGitignore_ExcludesNestedOut()
    {
        var root = TestDirs.NewTempDir();
        var webDir = Path.Combine(root, "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(root, ".gitignore"), "out/\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("out/file.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/out/x.js", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void Cascade_NestedGitignore_BeatsOuter()
    {
        var root = TestDirs.NewTempDir();
        var webDir = Path.Combine(root, "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(webDir, ".gitignore"), "!keep.log\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("app.log", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/keep.log", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void NestedGitignore_NonAnchoredPattern_DoesNotReachSiblingDirs()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "web"));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        File.WriteAllText(Path.Combine(root, "web", ".gitignore"), "x.js\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("web/x.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/deep/x.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("other/x.js", false).Excluded.ShouldBeFalse();
        ruleset.Decision("x.js", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void LastMatchWins_SqueezrignoreOverridesDefault()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".squeezrignore"), "!dist/keep-me.js\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("dist/bundle.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("dist/keep-me.js", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void RespectGitignore_False_GitignoreLayerIgnored()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(root, ".squeezrignore"), "*.tmp\n");

        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                RespectGitignore = false
            }
        };

        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.RespectsGitignore.ShouldBeFalse();
        ruleset.Decision("app.log", false).Excluded.ShouldBeFalse();
        ruleset.Decision("cache.tmp", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void RespectGitignore_True_GitignoreLayerApplied()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.RespectsGitignore.ShouldBeTrue();
        ruleset.Decision("app.log", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void Decision_ReturnsSourceAndPattern()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        var decision = ruleset.Decision("app.log", false);

        decision.Excluded.ShouldBeTrue();
        decision.Source.ShouldBe(IgnoreSource.Gitignore);
        decision.Pattern.ShouldBe("*.log");
    }

    [Fact]
    public void Decision_NotExcluded_HasNullSourceAndPattern()
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

        var decision = ruleset.Decision("src/app.cs", false);

        decision.Excluded.ShouldBeFalse();
        decision.Source.ShouldBeNull();
        decision.Pattern.ShouldBeNull();
    }

    [Fact]
    public void UnsupportedPattern_WarnsAndSkipped()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "[[:alpha:]].txt\n");

        var config = SqueezrConfig.CreateDefault();
        var warnings = new List<string>();
        var ruleset = IgnoreRuleset.For(root, config, w => warnings.Add(w));

        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("unsupported ignore pattern");
        warnings[0].ShouldContain("[[:alpha:]].txt");
        ruleset.Decision("a.txt", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void UnsupportedPattern_WithoutWarnSink_KeepsTheFileGitKeeps()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "[[:alpha:]]\n");

        var ruleset = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault());

        ruleset.Decision("a]", false).Excluded.ShouldBeFalse();
        ruleset.Decision("b", false).Excluded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("config")]
    [InlineData("squeezrignore")]
    [InlineData("gitignore")]
    public void UnsupportedPattern_FromEverySource_SameOutcomeWithAndWithoutWarnSink(string source)
    {
        var root = TestDirs.NewTempDir();
        var config = FixtureForUnsupportedSource(source, root);

        var warnings = new List<string>();
        var withSink = IgnoreRuleset.For(root, config, warnings.Add);
        var withoutSink = IgnoreRuleset.For(root, config);

        warnings.ShouldHaveSingleItem();
        warnings[0].ShouldContain("[[:alpha:]]");
        withSink.Decision("a]", false).Excluded.ShouldBeFalse();
        withoutSink.Decision("a]", false).Excluded.ShouldBeFalse();
        withSink.Decision("b.txt", false).Excluded.ShouldBeFalse();
        withoutSink.Decision("b.txt", false).Excluded.ShouldBeFalse();
        withSink.Patterns.ShouldNotContain(pattern => pattern.Unsupported);
        withoutSink.Patterns.ShouldNotContain(pattern => pattern.Unsupported);
    }

    [Fact]
    public void Evaluator_GivenUnsupportedPattern_DecidesNothing()
    {
        var pattern = GitignoreMatcher.Parse("[[:alpha:]]", IgnoreSource.Config, "config", ".")!;
        pattern.Unsupported.ShouldBeTrue();

        var rule = new IgnoreRule(pattern);

        rule.Matches(["a]"], ["a]"], 1, isDirectory: false).ShouldBeFalse();
        pattern.MatchesPathInternal("a]", isDirectory: false).ShouldBeFalse();
        pattern.Matches("a]", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void CharacterClassPattern_EntersTheCascadeAndExcludes()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "[Dd]ebug/\nGenerated\\ Files/\n");

        var warnings = new List<string>();
        var ruleset = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault(), w => warnings.Add(w));

        warnings.ShouldBeEmpty();
        ruleset.Decision("Debug/app.dll", false).Excluded.ShouldBeTrue();
        ruleset.Decision("Debug/app.dll", false).Source.ShouldBe(IgnoreSource.Gitignore);
        ruleset.Decision("Debug", false).Excluded.ShouldBeFalse();
        ruleset.Decision("Generated Files/model.cs", false).Excluded.ShouldBeTrue();
        ruleset.Decision("GeneratedFiles/model.cs", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void LegacyConfig_WithoutRespectGitignore_DefaultsToTrue()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = ConfigFile.Load(root);

        config.Indexing.RespectGitignore.ShouldBeTrue();
    }

    [Fact]
    public void NegateParentDirectory_ReincludesDirectory()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "out/\n!web/out/\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("out/file.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/out/x.js", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void NegateFileInsideExcludedDirectory_StillExcluded()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "out/\n!web/out/x.js\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("web/out/x.js", false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void NegateFileInsideNonExcludedDirectory_Works()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "web/out/*\n!web/out/keep.js\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("web/out/drop.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/out/keep.js", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void NegateInNestedGitignore_BeatsOuterPattern()
    {
        var root = TestDirs.NewTempDir();
        var webDir = Path.Combine(root, "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.js\n");
        File.WriteAllText(Path.Combine(webDir, ".gitignore"), "!x.js\n");

        var config = SqueezrConfig.CreateDefault();
        var ruleset = IgnoreRuleset.For(root, config);

        ruleset.Decision("app.js", false).Excluded.ShouldBeTrue();
        ruleset.Decision("web/x.js", false).Excluded.ShouldBeFalse();
    }

    private static SqueezrConfig FixtureForUnsupportedSource(string source, string root)
    {
        var indexing = SqueezrConfig.CreateDefault().Indexing;
        switch (source)
        {
            case "config":
                return SqueezrConfig.CreateDefault() with
                {
                    Indexing = indexing with { Ignore = ["[[:alpha:]]"], RespectGitignore = false }
                };
            case "squeezrignore":
                File.WriteAllText(Path.Combine(root, ".squeezrignore"), "[[:alpha:]]\n");
                return SqueezrConfig.CreateDefault() with
                {
                    Indexing = indexing with { Ignore = [], RespectGitignore = false }
                };
            default:
                File.WriteAllText(Path.Combine(root, ".gitignore"), "[[:alpha:]]\n");
                return SqueezrConfig.CreateDefault() with
                {
                    Indexing = indexing with { Ignore = [], RespectGitignore = true }
                };
        }
    }

    private static string NewWorkspaceWithConfig(string json)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
        return root;
    }
}
