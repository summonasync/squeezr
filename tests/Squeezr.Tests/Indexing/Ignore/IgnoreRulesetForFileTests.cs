using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Ignore;

public class IgnoreRulesetForFileTests
{
    private static readonly string[] FixtureFiles =
    [
        ".gitignore", ".squeezrignore", "app.log", "bin/.gitignore", "bin/tool.cs", "keep.log",
        "node_modules/.gitignore", "node_modules/keep.log", "obj/.gitignore", "obj/sub/deep.txt",
        "out/bundle.js", "root.cs", "secret/private.txt", "secret/public.txt", "src/.gitignore",
        "src/a/b/.gitignore", "src/a/b/hold.cs", "src/a/b/scratch.tmp", "src/a/keep.log",
        "src/app.log", "src/build/typed.cs", "src/keep.log", "web/out/x.js"
    ];

    [Fact]
    public void ForFile_DecisionsEqualFullCascade_OnEveryFilePath()
    {
        var root = BuildCascadeFixture();
        var config = SqueezrConfig.CreateDefault();
        var full = IgnoreRuleset.For(root, config);

        foreach (var path in AssertFixtureTree(root))
        {
            var expected = full.Decision(path, isDirectory: false);
            var actual = IgnoreRuleset.ForFile(root, config, path).Decision(path, isDirectory: false);

            actual.Excluded.ShouldBe(expected.Excluded, $"exclusion changed for '{path}'");
            actual.Source.ShouldBe(expected.Source, $"source changed for '{path}'");
            actual.Pattern.ShouldBe(expected.Pattern, $"pattern changed for '{path}'");
        }

        full.Decision("web/out/x.js", false).Excluded.ShouldBeTrue();
        full.Decision("src/a/b/scratch.tmp", false).Excluded.ShouldBeTrue();
        full.Decision("src/keep.log", false).Excluded.ShouldBeFalse();
    }

    [Fact]
    public void ForFile_ReadsGitignoreOnlyAlongTheAncestorChain()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, ".gitignore", "[Cc]overage/\n");
        for (var i = 0; i < 20; i++)
        {
            WriteFile(root, $"branch{i:D2}/.gitignore", "[Nn]ope/\n");
            WriteFile(root, $"branch{i:D2}/src/unit.cs", "class U { }");
        }

        var config = SqueezrConfig.CreateDefault();
        var cascadeWarnings = new List<string>();
        IgnoreRuleset.For(root, config, cascadeWarnings.Add);

        var chainWarnings = new List<string>();
        IgnoreRuleset.ForFile(root, config, "branch07/src/unit.cs", chainWarnings.Add);

        cascadeWarnings.Count.ShouldBe(21);
        chainWarnings.Count.ShouldBe(2);
        chainWarnings.ShouldContain(w => w.Contains(Path.Combine(root, ".gitignore")));
        chainWarnings.ShouldContain(w => w.Contains(Path.Combine(root, "branch07", ".gitignore")));
        chainWarnings.ShouldNotContain(w => w.Contains(Path.Combine(root, "branch00", ".gitignore")));
    }

    [Fact]
    public void ForFile_StopsAtScanPrunedDirectory_AndKeepsDefaultsAttribution()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "obj/.gitignore", "*.txt\n");
        WriteFile(root, "obj/sub/deep.txt", "text");

        var config = SqueezrConfig.CreateDefault();
        var warnings = new List<string>();
        var scoped = IgnoreRuleset.ForFile(root, config, "obj/sub/deep.txt", warnings.Add);
        var full = IgnoreRuleset.For(root, config);

        warnings.ShouldBeEmpty();
        var expected = full.Decision("obj/sub/deep.txt", isDirectory: false);
        var actual = scoped.Decision("obj/sub/deep.txt", isDirectory: false);

        actual.Excluded.ShouldBeTrue();
        actual.Source.ShouldBe(IgnoreSource.Defaults);
        actual.Source.ShouldBe(expected.Source);
        actual.Pattern.ShouldBe("**/obj/**");
        actual.Pattern.ShouldBe(expected.Pattern);
    }

    [Fact]
    public void ForFile_AppliesGitignoreOfTheFilesOwnDirectory()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/a/b/.gitignore", "*.tmp\n");
        WriteFile(root, "src/a/b/scratch.tmp", "temp");

        var config = SqueezrConfig.CreateDefault();

        IgnoreRuleset.ForFile(root, config, "src/a/b/scratch.tmp")
            .Decision("src/a/b/scratch.tmp", isDirectory: false).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void ForFile_FileAtRepoRoot_ReadsRootGitignoreOnly()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, ".gitignore", "*.log\n[Rr]oot/\n");
        WriteFile(root, "sibling/.gitignore", "[Ss]ibling/\n");

        var config = SqueezrConfig.CreateDefault();
        var warnings = new List<string>();
        var scoped = IgnoreRuleset.ForFile(root, config, "app.log", warnings.Add);

        scoped.Decision("app.log", isDirectory: false).Excluded.ShouldBeTrue();
        warnings.ShouldHaveSingleItem()
            .ShouldContain(Path.Combine(root, ".gitignore"));
    }

    [Fact]
    public void ForFile_RespectGitignoreFalse_KeepsConfiguredLayersOnly()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, ".gitignore", "src/\n");
        var defaultIndexing = SqueezrConfig.CreateDefault().Indexing;
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = defaultIndexing with { RespectGitignore = false }
        };

        var scoped = IgnoreRuleset.ForFile(root, config, "src/a/b/unit.cs");

        scoped.RespectsGitignore.ShouldBeFalse();
        scoped.Decision("src/a/b/unit.cs", isDirectory: false).Excluded.ShouldBeFalse();
        IgnoreRuleset.For(root, config)
            .Decision("src/a/b/unit.cs", isDirectory: false).Excluded.ShouldBeFalse();
    }

    private static string BuildCascadeFixture()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, ".gitignore", "out/\n*.log\n!keep.log\nsecret/\ngen/\n");
        WriteFile(root, "src/.gitignore", "!keep.log\nbuild/\n");
        WriteFile(root, "src/a/b/.gitignore", "*.tmp\n");
        WriteFile(root, "bin/.gitignore", "*.cs\n");
        WriteFile(root, "node_modules/.gitignore", "keep.log\n");
        WriteFile(root, "obj/.gitignore", "*.txt\n");
        WriteFile(root, ".squeezrignore", "!secret/public.txt\n");

        WriteFile(root, "root.cs", "class Root { }");
        WriteFile(root, "app.log", "log");
        WriteFile(root, "keep.log", "log");
        WriteFile(root, "out/bundle.js", "var x;");
        WriteFile(root, "src/app.log", "log");
        WriteFile(root, "src/keep.log", "log");
        WriteFile(root, "src/build/typed.cs", "class T { }");
        WriteFile(root, "src/a/keep.log", "log");
        WriteFile(root, "src/a/b/hold.cs", "class H { }");
        WriteFile(root, "src/a/b/scratch.tmp", "temp");
        WriteFile(root, "bin/tool.cs", "class Tool { }");
        WriteFile(root, "secret/private.txt", "priv");
        WriteFile(root, "secret/public.txt", "pub");
        WriteFile(root, "web/out/x.js", "var y;");
        WriteFile(root, "node_modules/keep.log", "log");
        WriteFile(root, "obj/sub/deep.txt", "text");
        return root;
    }

    private static string[] AssertFixtureTree(string root)
    {
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(FixtureFiles);
        return FixtureFiles;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
