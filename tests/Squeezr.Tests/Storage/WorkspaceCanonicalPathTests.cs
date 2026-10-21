using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public sealed class WorkspaceCanonicalPathTests
{
    [Fact]
    public void Canonicalize_PreservesPathRootWithTrailingSeparator()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        if (root is not { Length: > 0 })
        {
            return;
        }

        var canonical = WorkspaceCanonicalPath.Canonicalize(root);

        canonical.ShouldBe(root);
        canonical.ShouldEndWith(Path.DirectorySeparatorChar.ToString());
    }

    [Fact]
    public void Canonicalize_TrimsTrailingSeparatorsInsidePath()
    {
        var dir = TestDirs.NewTempDir();

        WorkspaceCanonicalPath.Canonicalize(dir + Path.DirectorySeparatorChar).ShouldBe(dir);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ResolveThroughLinks_SameDirectoryTwoSpellings_OneIdentity()
    {
        var scratch = TestDirs.NewTempDir();
        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(real);
        var store = Path.Combine(real, ".squeezr");
        Directory.CreateDirectory(store);
        Directory.CreateSymbolicLink(Path.Combine(scratch, "link"), real);

        WorkspaceCanonicalPath.ResolveThroughLinks(Path.Combine(scratch, "link", ".squeezr")).ShouldBe(store);
        WorkspaceCanonicalPath.ResolveThroughLinks(store).ShouldBe(store);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ResolveThroughLinks_ResolvesTheFinalComponentToo()
    {
        var scratch = TestDirs.NewTempDir();
        var real = Path.Combine(scratch, "target");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Path.Combine(scratch, "alias"), real);

        WorkspaceCanonicalPath.ResolveThroughLinks(Path.Combine(scratch, "alias"))
            .ShouldBe(WorkspaceCanonicalPath.Canonicalize(real));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ResolveThroughLinks_KeepsMissingTailLexicalAndCollapsesDotSegments()
    {
        var scratch = TestDirs.NewTempDir();
        var deep = Path.Combine(scratch, "a", "b");

        WorkspaceCanonicalPath.ResolveThroughLinks(deep).ShouldBe(Path.GetFullPath(deep));
        WorkspaceCanonicalPath.ResolveThroughLinks(Path.Combine(scratch, "a", "..", "x"))
            .ShouldBe(Path.Combine(scratch, "x"));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ResolveThroughLinks_RelayLoopPathsStopsWithoutThrowing()
    {
        var scratch = TestDirs.NewTempDir();
        Directory.CreateSymbolicLink(Path.Combine(scratch, "loop-a"), Path.Combine(scratch, "loop-b"));
        Directory.CreateSymbolicLink(Path.Combine(scratch, "loop-b"), Path.Combine(scratch, "loop-a"));

        var resolved = WorkspaceCanonicalPath.ResolveThroughLinks(Path.Combine(scratch, "loop-a", ".squeezr"));

        resolved.ShouldStartWith(scratch);
    }

    [Fact]
    public void Canonicalize_EveryFilesystemRoot_IsReturnedWithTrailingSeparator()
    {
        var roots = FilesystemRoots();
        roots.ShouldNotBeEmpty();

        foreach (var root in roots)
        {
            var canonical = WorkspaceCanonicalPath.Canonicalize(root);

            canonical.ShouldBe(root);
            canonical.ShouldEndWith(Path.DirectorySeparatorChar.ToString());
        }
    }

    private static List<string> FilesystemRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            // A drive that is mapped but disconnected, or an empty optical drive,
            // is listed but cannot be canonicalized meaningfully.
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToList();
        }

        var posixRoot = Path.GetPathRoot(Path.GetTempPath());
        return posixRoot is { Length: > 0 } ? [posixRoot] : [];
    }
}
