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

        ViolationsOf("M.cs", "throw new WorkspaceException(\"Invalid config, check the code field.\");")
            .ShouldHaveSingleItem();

        ViolationsOf("M.cs", "throw new WorkspaceException(\"has, xCode mapping\");")
            .ShouldHaveSingleItem();

        ViolationsOf("M.cs", "throw new WorkspaceException(\"Index is not built.\", reason);")
            .ShouldHaveSingleItem();

        ViolationsOf("M.cs", """
            throw new WorkspaceException("Index is not built.",
                WorkspaceException.IndexNotBuiltCode);
            """).ShouldBeEmpty();

        ViolationsOf("M.cs", "throw new WorkspaceException(msg, RegistryBusyCode);").ShouldBeEmpty();
        ViolationsOf("M.cs", "throw new WorkspaceException(msg, WorkspaceException.InitCodeConstant);").ShouldBeEmpty();
        ViolationsOf("M.cs", "throw new WorkspaceException(\"Index, or not, code is here\", IndexNotBuiltCode);")
            .ShouldBeEmpty();
    }

    [Fact]
    public void GuardSeesTheThrowSiteItWasWrittenFor()
    {
        var workspace = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Squeezr.Cli", "Mcp", "McpWorkspace.cs"));

        workspace.ShouldContain("new WorkspaceException(");
        ViolationsOf("McpWorkspace.cs", workspace).ShouldBeEmpty();
    }

    [Fact]
    public void McpLayer_EveryToolErrorSiteWiresTheInjectedWorkspace()
    {
        var sites = McpSourceFiles().SelectMany(ErrorCallSites).ToList();

        sites.Count.ShouldBe(6,
            "the MCP error envelope has exactly six producers; a new tool must be wired and counted here");
        sites.Select(site => site.Name).Distinct().Count().ShouldBe(6);

        sites.Where(site => !IsWiredToWorkspace(site.Arguments))
            .Select(site => $"{site.Name}:{site.Line}: ToolErrors.From({string.Join(", ", site.Arguments)})")
            .ShouldBeEmpty();
    }

    [Fact]
    public void WiringGuardIsNotBlind_ToTheShapesItOutlaws()
    {
        WiringViolations("return ToolErrors.From(ex);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, null);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, \"\");").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, \"/work/demo\");").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, root);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, other.Workspace);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, _workspace.Root);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, _workspaceDetached);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, _workspaceCache.Workspace);").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, _workspace ?? new McpWorkspace(r, r));").ShouldHaveSingleItem();
        WiringViolations("return ToolErrors.From(ex, _workspace);").ShouldBeEmpty();
        WiringViolations("return ToolErrors.From(ex, _workspace!);").ShouldBeEmpty();
        WiringViolations("return ToolErrors.From(\n    ex,\n    _workspace);").ShouldBeEmpty();
        WiringViolations("return ToolErrors.From(Resolve(\"a, b\"), _workspace);").ShouldBeEmpty();
    }

    [Fact]
    public void WiringGuardSeesTheSitesItWasWrittenFor()
    {
        var tool = Path.Combine(FindRepoRoot(), "src", "Squeezr.Cli", "Mcp", "Tools", "CodeSearchTool.cs");

        ErrorCallSites(tool).ShouldHaveSingleItem().Arguments.ShouldNotBeEmpty();
        WiringViolationsOf("CodeSearchTool.cs", File.ReadAllText(tool)).ShouldBeEmpty();
    }

    private record ErrorCallSite(string Name, int Line, List<string> Arguments);

    private static IEnumerable<ErrorCallSite> ErrorCallSites(string file)
    {
        var source = File.ReadAllText(file);
        return from site in ScanCallSites(source)
               select new ErrorCallSite(
                   Path.GetFileName(file),
                   source[..site.Index].Count(c => c == '\n') + 1,
                   site.Arguments);
    }

    private static List<string> WiringViolationsOf(string name, string source) =>
        (from site in ScanCallSites(source)
         where !IsWiredToWorkspace(site.Arguments)
         select $"{name}: ToolErrors.From({string.Join(", ", site.Arguments)})").ToList();

    private static List<string> WiringViolations(string statement) =>
        WiringViolationsOf("T.cs", statement);

    private static IEnumerable<(int Index, List<string> Arguments)> ScanCallSites(string source)
    {
        foreach (Match match in ToolErrorCall().Matches(source))
        {
            var open = source.IndexOf('(', match.Index);
            yield return (match.Index, SplitArguments(StripStringLiterals(source[open..])));
        }
    }

    private static bool IsWiredToWorkspace(List<string> arguments) =>
        arguments is [_, "_workspace" or "_workspace!"];

    private static string StripStringLiterals(string source) => StringLiteral().Replace(source, "\"\"");

    private static List<string> SplitArguments(string fromOpenParen)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 1;
        for (var i = 0; i < fromOpenParen.Length; i++)
        {
            var c = fromOpenParen[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (--depth == 0)
                {
                    Add(arguments, fromOpenParen[start..i], start == i);
                    return arguments;
                }
            }
            else if (c == ',' && depth == 1)
            {
                Add(arguments, fromOpenParen[start..i], false);
                start = i + 1;
            }
        }

        return arguments;
    }

    private static void Add(List<string> arguments, string raw, bool skipEmpty)
    {
        if (!skipEmpty)
        {
            arguments.Add(raw.Trim());
        }
    }

    [GeneratedRegex(@"ToolErrors\.From\s*\(")]
    private static partial Regex ToolErrorCall();

    [GeneratedRegex(@"""(?:\\.|[^""\\])*""")]
    private static partial Regex StringLiteral();

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
            var open = source.IndexOf('(', match.Index);
            var arguments = SplitArguments(StripStringLiterals(source[open..]));
            var tail = source[match.Index..];
            var end = tail.IndexOf(';');
            var statement = (end < 0 ? tail : tail[..end]).ReplaceLineEndings(" ").Trim();
            var line = source[..match.Index].Count(c => c == '\n') + 1;
            var code = arguments.Count > 1 ? arguments[1] : string.Empty;

            if (arguments.Count < 2 || !CodeArgument().IsMatch(code) || DefaultCodeRef().IsMatch(code))
            {
                yield return $"{name}:{line}: {statement}";
            }
        }
    }

    [GeneratedRegex(@"new\s+WorkspaceException\s*\(")]
    private static partial Regex Construction();

    [GeneratedRegex(@"\b\w*Code\w*\b|\bcode\b")]
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
