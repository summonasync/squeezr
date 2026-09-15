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
}
