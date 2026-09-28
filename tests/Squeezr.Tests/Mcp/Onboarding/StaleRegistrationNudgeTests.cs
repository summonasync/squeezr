using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class StaleRegistrationNudgeTests
{
    [Fact]
    public void NoLink_Silent()
    {
        var home = TestDirs.NewTempDir();
        var writer = new StringWriter();

        StaleRegistrationNudge.TryWrite(writer, home, "/opt/squeezr/squeezr");

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void NullProcessPath_Silent()
    {
        var home = TestDirs.NewTempDir();
        var writer = new StringWriter();

        StaleRegistrationNudge.TryWrite(writer, home, null);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void MatchThroughSymlink_Silent()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = TestDirs.NewTempDir();
        var versioned = Path.Combine(home, "apps", "v1.0.3", "squeezr");
        Directory.CreateDirectory(Path.GetDirectoryName(versioned)!);
        File.WriteAllText(versioned, "binary");
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        File.CreateSymbolicLink(Path.Combine(binDir, "squeezr"), versioned);

        var writer = new StringWriter();

        StaleRegistrationNudge.TryWrite(writer, home, versioned);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Mismatch_WritesSingleLineWithRepairCommand()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = TestDirs.NewTempDir();
        var current = Path.Combine(home, "apps", "v1.0.3", "squeezr");
        var stale = Path.Combine(home, "apps", "v1.0.2", "squeezr");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(current, "new-binary");
        File.WriteAllText(stale, "old-binary");
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        File.CreateSymbolicLink(Path.Combine(binDir, "squeezr"), current);

        var writer = new StringWriter();

        StaleRegistrationNudge.TryWrite(writer, home, stale);

        var output = writer.ToString();
        var line = output.TrimEnd('\n', '\r');
        line.ShouldContain("mcp install");
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
    }

    [Fact]
    public void SymlinkCycle_SilentNoThrow()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = TestDirs.NewTempDir();
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        var appsDir = Path.Combine(home, "apps");
        Directory.CreateDirectory(appsDir);
        var linkA = Path.Combine(binDir, "squeezr");
        var linkB = Path.Combine(appsDir, "squeezr-cycle");
        File.CreateSymbolicLink(linkA, linkB);
        File.CreateSymbolicLink(linkB, linkA);

        var writer = new StringWriter();

        Should.NotThrow(() => StaleRegistrationNudge.TryWrite(writer, home, linkA));

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void RunningBinaryIsLinkTarget_Silent()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = TestDirs.NewTempDir();
        var versioned = Path.Combine(home, "releases", "v2.0.0", "squeezr");
        Directory.CreateDirectory(Path.GetDirectoryName(versioned)!);
        File.WriteAllText(versioned, "binary");
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        var linkPath = Path.Combine(binDir, "squeezr");
        File.CreateSymbolicLink(linkPath, versioned);

        var writer = new StringWriter();

        StaleRegistrationNudge.TryWrite(writer, home, versioned);

        writer.ToString().ShouldBeEmpty();
    }
}
