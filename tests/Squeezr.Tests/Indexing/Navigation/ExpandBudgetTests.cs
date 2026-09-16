using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

[Collection("CliProcessGlobals")]
public class ExpandBudgetTests
{
    [Fact]
    public void Expand_BudgetSmallerThanSpan_TruncatesSpan()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand("src/Wide.cs", 2, 9, 0, null, 12);

        result.Truncated.ShouldBeTrue();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(12);
        result.Span.Count.ShouldBeLessThan(8);
        result.Before.ShouldBeEmpty();
        result.After.ShouldBeEmpty();
    }

    [Fact]
    public void Expand_BudgetCoversSpanButNotContext_TruncatesContext()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand("src/Wide.cs", 2, 3, 5, null, 20);

        result.Truncated.ShouldBeTrue();
        result.Span.Count.ShouldBe(2);
        result.TokensUsed.ShouldBeLessThanOrEqualTo(20);
        result.Before.Count.ShouldBeLessThan(5);
        result.After.Count.ShouldBeLessThan(5);
    }

    [Fact]
    public void Expand_BudgetCoversEverything_NotTruncated()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SpanExpander(workspace).Expand("src/Wide.cs", 2, 9, 3, null, 1000);

        result.Truncated.ShouldBeFalse();
        result.Span.Count.ShouldBe(8);
        result.Before.Count.ShouldBe(1);
        result.After.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Expand_CliJsonAndPretty_ReflectTruncated()
    {
        var workspace = NewIndexedWorkspace();

        var json = await RunCliAsync(workspace, "expand", "src/Wide.cs", "2", "9", "--context", "3", "--max-tokens", "12", "--json");
        json.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(json.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
            doc.RootElement.GetProperty("tokensUsed").GetInt32().ShouldBeLessThanOrEqualTo(12);
        }

        var pretty = await RunCliAsync(workspace, "expand", "src/Wide.cs", "2", "9", "--context", "3", "--max-tokens", "12");
        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("truncated: output cut at --max-tokens");

        var fits = await RunCliAsync(workspace, "expand", "src/Wide.cs", "2", "3", "--max-tokens", "1000", "--json");
        using (var doc = JsonDocument.Parse(fits.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        }
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Wide.cs", """
            namespace Demo;

            public class Wide
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Echo(string value)
                {
                    return value;
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
