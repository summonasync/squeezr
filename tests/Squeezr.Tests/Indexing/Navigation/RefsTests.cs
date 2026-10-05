using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

[Collection("CliProcessGlobals")]
public class RefsTests
{
    [Fact]
    public void Refs_CalledFromTwoFiles_ReturnsAllCallerPoints()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RefLookup(workspace).Find("Add", null);

        var allRefs = result.Groups.SelectMany(g => g.Refs).ToList();
        allRefs.Count.ShouldBe(2);
        allRefs.Select(r => r.File).OrderBy(f => f).ShouldBe(["src/Other.cs", "src/Runner.cs"]);
        allRefs.ShouldAllBe(r => r.Kind == "call" && r.Line > 0);
    }

    [Fact]
    public void Refs_Overloads_ResolveToSeparateGroups()
    {
        var workspace = NewIndexedWorkspace();

        var result = new RefLookup(workspace).Find("Add", null);

        result.Groups.Count.ShouldBe(2);
        result.Groups.ShouldAllBe(g => g.Definitions.ShouldHaveSingleItem().File == "src/Calc.cs" && g.Refs.Count == 1);
        result.Groups.Select(g => g.Refs[0].File).OrderBy(f => f).ShouldBe(["src/Other.cs", "src/Runner.cs"]);
    }

    [Fact]
    public void Refs_UnknownSymbol_Throws()
    {
        var workspace = NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(() => new RefLookup(workspace).Find("Nope", null));
        ex.Message.ShouldContain("No symbol named 'Nope'");
    }

    [Fact]
    public async Task Refs_Cli_PrettyAndJsonOutputs()
    {
        var root = NewIndexedWorkspace();

        var pretty = await RunCliAsync(root, "refs", "Add");
        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("src/Runner.cs:");
        pretty.StdOut.ShouldContain("src/Other.cs:");
        pretty.StdOut.ShouldContain(" call");
        pretty.StdOut.ShouldContain("in Runner.Go");

        var json = await RunCliAsync(root, "refs", "Add", "--json");
        json.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(json.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("symbol").GetString().ShouldBe("Add");
            var refs = doc.RootElement.GetProperty("refs");
            refs.GetArrayLength().ShouldBe(2);
            refs[0].GetProperty("file").GetString()!.ShouldStartWith("src/");
            refs[0].GetProperty("line").GetInt32().ShouldBeGreaterThan(0);
            refs[0].GetProperty("kind").GetString().ShouldBe("call");
            refs[0].GetProperty("enclosingSymbol").GetString().ShouldBe("Runner.Go");
            refs[1].GetProperty("enclosingSymbol").GetString().ShouldBe("Other.Combine");
        }

        var missing = await RunCliAsync(root, "refs", "Nope", "--json");
        missing.ExitCode.ShouldBe(1);
        missing.StdOut.ShouldBeEmpty();
        using (var doc = JsonDocument.Parse(missing.StdErr.Trim()))
        {
            var error = doc.RootElement.GetProperty("error");
            error.GetProperty("code").GetString().ShouldBe("workspace_error");
            error.GetProperty("message").GetString()!.ShouldContain("No symbol named 'Nope'");
        }
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Other.cs", """
            namespace Demo;

            public class Other
            {
                public string Combine(Calc calc)
                {
                    return calc.Add("x", "y");
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
