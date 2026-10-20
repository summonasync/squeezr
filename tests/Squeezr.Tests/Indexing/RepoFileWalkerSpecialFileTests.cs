using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing;

public class RepoFileWalkerSpecialFileTests
{
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Walk_TreeContainingWriterlessFifo_SkipsChannelAndCompletes()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "src/keep.cs", "class Kept { }");
        var fifo = Path.Combine(root, "src", "stdin.fifo");
        Assert.Equal(0, mkfifo(fifo, 0b100_100_100));

        // the gate rides on this probe: if a regression ever answers true for a FIFO, the walk below
        // would block on open() with no writer — this assert turns that into a fast failure instead
        // of a wedged suite
        UnixFileType.IsRegularFile(fifo).ShouldBeFalse();
        UnixFileType.IsRegularFile(Path.Combine(root, "src", "keep.cs")).ShouldBeTrue();

        var warnings = new List<string>();
        var config = SqueezrConfig.CreateDefault();
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(
            root, IgnoreRuleset.For(root, config), SecretGuardIngestion.Load(root),
            config.Indexing.MaxFileSizeKb, warnings.Add);

        result.Files.Select(f => f.RelativePath).ShouldBe(["src/keep.cs"]);
        result.Skipped.ShouldBe(1);
        result.UnreadableFiles.ShouldBeEmpty();
        warnings.ShouldContain(w =>
            w.Contains("src/stdin.fifo", StringComparison.Ordinal)
            && w.Contains("not a regular file", StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Walk_WriterlessFifoBesideRegularFiles_SkipsChannelInRootToo()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "a.cs", "class A { }");
        Assert.Equal(0, mkfifo(Path.Combine(root, "pipe.fifo"), 0b100_100_100));

        var warnings = new List<string>();
        var config = SqueezrConfig.CreateDefault();
        var result = Squeezr.Core.Indexing.RepoFileWalker.Walk(
            root, IgnoreRuleset.For(root, config), SecretGuardIngestion.Load(root),
            config.Indexing.MaxFileSizeKb, warnings.Add);

        result.Files.Select(f => f.RelativePath).ShouldBe(["a.cs"]);
        result.Skipped.ShouldBe(1);
        warnings.ShouldContain(w => w.Contains("pipe.fifo", StringComparison.Ordinal));
    }

    [Fact]
    public void Walk_CanceledToken_ThrowsAtFirstEntry()
    {
        var root = TestDirs.NewTempDir();
        WriteFile(root, "a.cs", "class A { }");
        var config = SqueezrConfig.CreateDefault();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            Squeezr.Core.Indexing.RepoFileWalker.Walk(
                root, IgnoreRuleset.For(root, config), SecretGuardIngestion.Load(root),
                config.Indexing.MaxFileSizeKb, cancellationToken: cts.Token));
    }

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
