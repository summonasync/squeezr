using Shouldly;
using Squeezr.Cli.Mcp;

namespace Squeezr.Tests.Mcp;

public class SessionLocationTests
{
    [Theory]
    [InlineData("/work/demo", "/work/demo", true)]
    [InlineData("/work/demo/", "/work/demo", true)]
    [InlineData("/work/demo", "/work/demo/", true)]
    [InlineData("/work/demo", "/work/demo/src/app", true)]
    [InlineData("/work/demo", "/work/demo2", false)]
    [InlineData("/work/demo", "/work", false)]
    [InlineData("/work/demo", "/other/demo", false)]
    [InlineData("/work/demo", "/other/demo/src", false)]
    public void IsWithinRoot_KnowsWhereTheSessionSits(string root, string session, bool expected) =>
        SessionLocation.IsWithinRoot(root, session).ShouldBe(expected);

    [Fact]
    public void IsWithinRoot_RealNestedDirectoryAndSelfAreInside()
    {
        var root = TestDirs.NewTempDir();
        var nested = Path.Combine(root, "src");
        Directory.CreateDirectory(nested);

        SessionLocation.IsWithinRoot(root, root).ShouldBeTrue();
        SessionLocation.IsWithinRoot(root, nested).ShouldBeTrue();
    }

    [Fact]
    public void IsWithinRoot_SiblingScratchDirectoryIsOutside()
    {
        SessionLocation.IsWithinRoot(TestDirs.NewTempDir(), TestDirs.NewTempDir()).ShouldBeFalse();
    }

    [Fact]
    public void Display_CanonicalizesAndDropsTrailingSeparators()
    {
        var root = TestDirs.NewTempDir();

        SessionLocation.Display(root + Path.DirectorySeparatorChar).ShouldBe(root);
        SessionLocation.Display("/work/demo/").ShouldBe("/work/demo");
    }
}
