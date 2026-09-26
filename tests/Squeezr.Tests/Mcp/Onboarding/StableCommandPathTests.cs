using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class StableCommandPathTests
{
    [Fact]
    public void Resolve_WithExistingLink_ReturnsLinkPath()
    {
        var home = TestDirs.NewTempDir();
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        var linkPath = Path.Combine(binDir, "squeezr");
        File.WriteAllText(linkPath, "fake-binary");

        var result = StableCommandPath.Resolve(home, "/opt/versioned/squeezr");

        result.ShouldBe(linkPath);
    }

    [Fact]
    public void Resolve_WithSymlinkToVersionedBinary_ReturnsLinkPath()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = TestDirs.NewTempDir();
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        var versioned = Path.Combine(home, "apps", "squeezr", "v1.0.3", "squeezr");
        Directory.CreateDirectory(Path.GetDirectoryName(versioned)!);
        File.WriteAllText(versioned, "binary");
        var linkPath = Path.Combine(binDir, "squeezr");
        File.CreateSymbolicLink(linkPath, versioned);

        var result = StableCommandPath.Resolve(home, versioned);

        result.ShouldBe(linkPath);
        result.ShouldNotBe(versioned);
    }

    [Fact]
    public void Resolve_WithoutLink_ReturnsProcessPath()
    {
        var home = TestDirs.NewTempDir();
        var processPath = "/opt/squeezr/bin/squeezr";

        var result = StableCommandPath.Resolve(home, processPath);

        result.ShouldBe(processPath);
    }

    [Fact]
    public void Resolve_NullProcessPathWithoutLink_ThrowsInvalidOperation()
    {
        var home = TestDirs.NewTempDir();

        Should.Throw<InvalidOperationException>(() => StableCommandPath.Resolve(home, null));
    }

    [Fact]
    public void Resolve_NullProcessPathWithLink_ReturnsLink()
    {
        var home = TestDirs.NewTempDir();
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "squeezr"), "fake");

        var result = StableCommandPath.Resolve(home, null);

        result.ShouldBe(Path.Combine(binDir, "squeezr"));
    }

    [Fact]
    public void Resolve_WithBrokenSymlink_ReturnsProcessPath()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = TestDirs.NewTempDir();
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        var linkPath = Path.Combine(binDir, "squeezr");
        var target = Path.Combine(home, "versioned", "squeezr");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "binary");
        File.CreateSymbolicLink(linkPath, target);
        File.Delete(target);

        var result = StableCommandPath.Resolve(home, "/opt/fallback/squeezr");

        result.ShouldBe("/opt/fallback/squeezr");
    }

    [Fact]
    public void Resolve_DevScenario_NoLinkInHome_ReturnsProcessPath()
    {
        var home = TestDirs.NewTempDir();
        var artifactsPath = Path.Combine(home, "artifacts", "squeezr");

        var result = StableCommandPath.Resolve(home, artifactsPath);

        result.ShouldBe(artifactsPath);
    }
}
