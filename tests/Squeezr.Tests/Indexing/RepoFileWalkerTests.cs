using Shouldly;
using Squeezr.Core.Configuration;

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
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, config.Indexing.Ignore, config.Indexing.MaxFileSizeKb);

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
    public void Walk_WithoutMatchingGlobs_IndexesEverything()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "a.cs", "class A { }");
        WriteFile(root, "sub/b.js", "function b() { }");

        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, [], 512);

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

        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, [], 512, warnings.Add);

        result.Files.Select(f => f.RelativePath).ShouldBe(["free.cs"]);
        result.Skipped.ShouldBe(1);
        warnings.ShouldHaveSingleItem().ShouldContain("locked.cs");
    }

    [Fact]
    public void Walk_SymlinkedDirectory_IsNotFollowed()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "real/a.cs", "class A { }");
        WriteFile(root, "outside/b.cs", "class B { }");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(root, [], 512);

        result.Files.Select(f => f.RelativePath).ShouldBe(["real/a.cs"]);
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
