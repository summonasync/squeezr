using Shouldly;
using Squeezr.Cli.Mcp;

namespace Squeezr.Tests.Mcp;

public class McpServerHostTests
{
    [Fact]
    public void SessionAtWorkspaceRoot_NoWarning()
    {
        var root = NewDir();

        var output = Warn(root, root);

        output.ShouldBe(string.Empty);
    }

    [Fact]
    public void SessionNestedInsideWorkspace_NoWarning()
    {
        var root = NewDir();
        var nested = NewDir(Path.Combine(root, "src", "lib"));

        var output = Warn(root, nested);

        output.ShouldBe(string.Empty);
    }

    [Fact]
    public void SessionOutsideWorkspace_PrintsSingleWarningLine()
    {
        var root = NewDir();
        var outside = NewDir();

        var output = Warn(root, outside);

        output.ShouldBe(
            $"warning: session directory '{outside}' is outside the workspace root '{root}'; " +
            "tools serve that workspace's index, not the current project" + Environment.NewLine);
    }

    [Fact]
    public void SessionOnSiblingWithSharedPrefix_Warns()
    {
        var parent = NewDir();
        var root = NewDir(Path.Combine(parent, "ws"));
        var sibling = NewDir(Path.Combine(parent, "ws-other"));

        var output = Warn(root, sibling);

        output.ShouldContain("warning:");
    }

    private static string Warn(string workspaceRoot, string sessionDirectory)
    {
        var writer = new StringWriter();
        McpServerHost.WarnIfSessionOutsideWorkspace(workspaceRoot, sessionDirectory, writer);
        return writer.ToString();
    }

    private static string NewDir(string? path = null)
    {
        var dir = Path.GetFullPath(path ?? TestDirs.NewTempDir());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
