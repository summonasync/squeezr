using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing;

public class RepoFileWalkerTests
{
    [Fact]
    public void Walk_AppliesIgnoreGlobsAndSkipsBinaryAndOversize()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/Program.cs", "class Program { }");
        WriteFile(root, "src/app.js", "function main() { return 1; }");
        WriteFile(root, "docs/readme.md", "# docs");
        WriteFile(root, "bundle.min.js", "var x=1;");
        WriteFile(root, "node_modules/dep/index.js", "module.exports = {};");
        WriteFile(root, ".git/config", "[core]");
        WriteFile(root, ".squeezr/config.json", "{}");
        WriteFile(root, "data/blob.bin", new byte[] { 1, 2, 0, 4, 5 });
        WriteFile(root, "logs/big.log", new string('x', 600 * 1024));

        var config = SqueezrConfig.CreateDefault();
        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Files.Select(f => f.RelativePath).ShouldBe([
            "docs/readme.md",
            "src/Program.cs",
            "src/app.js"
        ]);
        result.Skipped.ShouldBe(2);

        var byPath = result.Files.ToDictionary(f => f.RelativePath);
        byPath["src/Program.cs"].Language.ShouldBe("csharp");
        byPath["src/app.js"].Language.ShouldBe("javascript");
        byPath["docs/readme.md"].Language.ShouldBe("markdown");
        byPath["src/Program.cs"].Sha256.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Walk_DefaultConfig_SkipsIdeJunkFolders()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/Program.cs", "class Program { }");
        WriteFile(root, ".vs/Slurpy.slnx/FileContentIndex/index.vsidx", "idx");
        WriteFile(root, ".idea/workspace.xml", "<project />");

        var config = SqueezrConfig.CreateDefault();
        config.Indexing.Ignore.ShouldContain("**/.vs/**");
        config.Indexing.Ignore.ShouldContain("**/.idea/**");

        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Files.Select(f => f.RelativePath).ShouldBe(["src/Program.cs"]);
        result.Skipped.ShouldBe(0);
    }

    [Fact]
    public void Walk_WithoutMatchingGlobs_IndexesEverything()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "a.cs", "class A { }");
        WriteFile(root, "sub/b.js", "function b() { }");

        var rules = EmptyRuleset(root);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, 512);

        result.Files.Select(f => f.RelativePath).ShouldBe(["a.cs", "sub/b.js"]);
        result.Skipped.ShouldBe(0);
    }

    [Fact]
    public void Walk_LockedFile_IsSkippedWithoutCrashing()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "free.cs", "class Free { }");
        var lockedPath = Path.Combine(root, "locked.cs");
        File.WriteAllText(lockedPath, "class Locked { }");

        using var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var warnings = new List<string>();

        var rules = EmptyRuleset(root);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, 512, warnings.Add);

        result.Files.Select(f => f.RelativePath).ShouldBe(["free.cs"]);
        result.Skipped.ShouldBe(1);
        warnings.ShouldHaveSingleItem().ShouldContain("locked.cs");
    }

    [Fact]
    public void Walk_SymlinkedDirectory_IsNotFollowed()
    {
        var baseDir = TestDirs.NewTempDir();
        var root = Path.Combine(baseDir, "root");
        var outside = Path.Combine(baseDir, "outside");
        WriteFile(root, "real/a.cs", "class A { }");
        WriteFile(outside, "b.cs", "class B { }");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var rules = EmptyRuleset(root);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, 512);

        result.Files.Select(f => f.RelativePath).ShouldBe(["real/a.cs"]);
    }

    [Fact]
    public void Walk_DirRule_DoesNotDescend_SubtreeFilesNeverRead()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/keep.cs", "class Keep { }");
        WriteFile(root, "web/out/deep.js", "function deep() { }");
        WriteFile(root, "web/out/vendor/lib.js", "function lib() { }");
        WriteFile(root, ".gitignore", "web/out/\n");

        var config = SqueezrConfig.CreateDefault();
        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Files.Select(f => f.RelativePath).ShouldBe([".gitignore", "src/keep.cs"]);
        result.Ignored.ShouldBe(2);
        result.IgnoredBySource.ShouldContainKey(IgnoreSource.Gitignore);
        result.IgnoredBySource[IgnoreSource.Gitignore].ShouldBe(2);
    }

    [Fact]
    public void Walk_PrunedDirectory_CountsFilesByName_WithoutReadingOrHashingThem()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/keep.cs", "class Keep { }");
        WriteFile(root, "build/out.js", "var x;");
        WriteFile(root, "build/nested/deep.js", "function deep() { }");
        WriteFile(root, "build/huge.log", new string('x', 900 * 1024));
        WriteFile(root, ".gitignore", "build/\n");

        var config = SqueezrConfig.CreateDefault();
        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Ignored.ShouldBe(3);
        result.Skipped.ShouldBe(0);
        result.Files.Select(f => f.RelativePath).ShouldBe([".gitignore", "src/keep.cs"]);
    }

    [Fact]
    public void Walk_ReportsIgnoredBySource()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/keep.cs", "class Keep { }");
        WriteFile(root, "build/out.js", "var x;");
        WriteFile(root, "build/nested/deep.js", "function deep() { }");
        WriteFile(root, ".gitignore", "build/\n");

        var config = SqueezrConfig.CreateDefault();
        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Ignored.ShouldBe(2);
        result.IgnoredBySource[IgnoreSource.Gitignore].ShouldBe(2);
        result.IgnoredBySource.Values.Sum().ShouldBe(result.Ignored);
    }

    [Fact]
    public void Walk_EmptyPrunedDirectory_CountsNoFiles()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/keep.cs", "class Keep { }");
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        WriteFile(root, ".gitignore", "artifacts/\n");

        var config = SqueezrConfig.CreateDefault();
        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Ignored.ShouldBe(0);
        result.IgnoredBySource.ShouldNotContainKey(IgnoreSource.Gitignore);
    }

    [Fact]
    public void Walk_RespectGitignoreFalse_IgnoresOnlyDefaults()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/keep.cs", "class Keep { }");
        WriteFile(root, "build/deep.js", "function deep() { }");
        WriteFile(root, ".gitignore", "build/\n");

        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with { RespectGitignore = false }
        };
        var rules = IgnoreRuleset.For(root, config);
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, rules, config.Indexing.MaxFileSizeKb);

        result.Files.Select(f => f.RelativePath).ShouldContain("build/deep.js");
    }

    private static IgnoreRuleset EmptyRuleset(string root)
    {
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = [],
                RespectGitignore = false
            }
        };
        return IgnoreRuleset.For(root, config);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void WriteFile(string root, string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }
}
