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
