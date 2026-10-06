using System.Text.RegularExpressions;
using Shouldly;

namespace Squeezr.Tests.Mcp;

public sealed partial class McpThrowSiteGuardTests
{
    [Fact]
    public void McpLayer_NoWorkspaceExceptionIsBuiltWithoutAnExplicitCode()
    {
        var offenders = McpSourceFiles().SelectMany(Violations).ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void GuardIsNotBlind_ToTheDefaultCodeShapesItOutlaws()
    {
        ViolationsOf("M.cs", "throw new WorkspaceException(\"Workspace is not initialized.\");")
            .ShouldHaveSingleItem();

        ViolationsOf("M.cs", "throw new WorkspaceException(msg, WorkspaceException.DefaultCode);")
            .ShouldHaveSingleItem();

        ViolationsOf("M.cs", """
            throw new WorkspaceException("Index is not built.",
                WorkspaceException.IndexNotBuiltCode);
            """).ShouldBeEmpty();

        ViolationsOf("M.cs", "throw new WorkspaceException(msg, RegistryBusyCode);").ShouldBeEmpty();
    }

    [Fact]
    public void GuardSeesTheThrowSiteItWasWrittenFor()
    {
        var workspace = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Squeezr.Cli", "Mcp", "McpWorkspace.cs"));

        workspace.ShouldContain("new WorkspaceException(");
        ViolationsOf("McpWorkspace.cs", workspace).ShouldBeEmpty();
    }

    private static IEnumerable<string> McpSourceFiles()
    {
        var mcpDir = Path.Combine(FindRepoRoot(), "src", "Squeezr.Cli", "Mcp");
        Directory.Exists(mcpDir).ShouldBeTrue();
        return Directory.EnumerateFiles(mcpDir, "*.cs", SearchOption.AllDirectories);
    }

    private static List<string> ViolationsOf(string name, string source) =>
        ViolationsFor(name, source).ToList();

    private static IEnumerable<string> Violations(string file) =>
        ViolationsFor(Path.GetFileName(file), File.ReadAllText(file));

    private static IEnumerable<string> ViolationsFor(string name, string source)
    {
        foreach (Match match in Construction().Matches(source))
        {
            var tail = source[match.Index..];
            var end = tail.IndexOf(';');
            var statement = (end < 0 ? tail : tail[..end]).ReplaceLineEndings(" ").Trim();
            var line = source[..match.Index].Count(c => c == '\n') + 1;
            var carriesCode = statement.Contains(',') && CodeArgument().IsMatch(statement);

            if (!carriesCode || DefaultCodeRef().IsMatch(statement))
            {
                yield return $"{name}:{line}: {statement}";
            }
        }
    }

    [GeneratedRegex(@"new\s+WorkspaceException\s*\(")]
    private static partial Regex Construction();

    [GeneratedRegex(@"\bWorkspaceException\.\w+Code\b|\b\w+Code\b|\bcode\b")]
    private static partial Regex CodeArgument();

    [GeneratedRegex(@"\bDefaultCode\b")]
    private static partial Regex DefaultCodeRef();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}; " +
            "the MCP throw-site guard cannot run outside a checkout.");
    }
}
