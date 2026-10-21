using System.Text.Json;
using Shouldly;
using Squeezr.Cli;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Mcp;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class QueryTelemetryInstrumentationTests
{
    [Fact]
    public async Task Search_Json_AppendsSingleRecordWithActualValues()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        var search = await RunCliAsync(root, "search", "foo", "--json");
        search.ExitCode.ShouldBe(0);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(1);
        var entry = entries.Single();
        entry.Tool.ShouldBe("search");
        entry.SessionId.ShouldNotBe(Guid.Empty);
        entry.TokensUsed.ShouldBeGreaterThan(0);
        entry.Truncated.ShouldBeFalse();
        entry.Hits.ShouldBe(1);
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("query").GetString().ShouldBe("foo");
        parameters.GetProperty("regex").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Expand_AppendsRecordWithFileParam()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        var expand = await RunCliAsync(root, "expand", "src/Foo.cs", "1");
        expand.ExitCode.ShouldBe(0);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(1);
        var entry = entries.Single();
        entry.Tool.ShouldBe("expand");
        entry.SessionId.ShouldNotBe(Guid.Empty);
        entry.TokensUsed.ShouldBeGreaterThan(0);
        entry.Hits.ShouldBe(1);
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("file").GetString().ShouldBe("src/Foo.cs");
        parameters.GetProperty("startLine").GetInt32().ShouldBe(1);
        parameters.GetProperty("endLine").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Search_Failure_DoesNotAppendTelemetry()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        (await RunCliAsync(root, "search", "foo", "--json")).ExitCode.ShouldBe(0);
        QueryTelemetry.Count(root).ShouldBe(1);

        File.WriteAllBytes(
            SqueezrPaths.TrigramIndexPath(root),
            Enumerable.Repeat((byte)0xAA, 40).ToArray());
        var failed = await RunCliAsync(root, "search", "foo", "--json");
        failed.ExitCode.ShouldBe(1);

        QueryTelemetry.Count(root).ShouldBe(1);
    }

    [Fact]
    public async Task McpToolCalls_ShareSessionIdWithinConnection()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        var search = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);
        search.IsError.ShouldNotBe(true);

        var expand = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 3 },
            cancellationToken: CancellationToken.None);
        expand.IsError.ShouldNotBe(true);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e.SessionId != Guid.Empty);
        entries.Select(e => e.SessionId).Distinct().Count().ShouldBe(1);
        entries.Select(e => e.Tool).OrderBy(t => t, StringComparer.Ordinal)
            .ShouldBe(["code_search", "expand_span"]);
        var parameters = (JsonElement)entries.Single(e => e.Tool == "expand_span").Params!;
        parameters.GetProperty("file").GetString().ShouldBe("src/Calc.cs");
    }

    [Fact]
    public void QuerySession_Id_StaysConstantWithinProcess()
    {
        QuerySession.Id.ShouldNotBe(Guid.Empty);
        QuerySession.Id.ShouldBe(QuerySession.Id);
    }

    private static async Task<string> NewIndexedCliWorkspaceAsync()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        WriteFile(root, "src/Foo.cs", "public class Foo { string foo = \"foobody\"; }");
        (await RunCliAsync(root, "add", ".", "--name", "dot", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
        return root;
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
