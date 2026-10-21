using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

[Collection("ProcessGlobals")]
public class SymbolDidYouMeanTests
{
    [Fact]
    public void NearName_ReturnsCandidatesWithDeterministicOrder()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find(
            "MaterializationServce", null, null, SymbolLookup.DefaultMaxTokens, body: false);

        result.Matches.ShouldBeEmpty();
        result.DidYouMean.ShouldNotBeNull();
        result.DidYouMean.ShouldContain("Materializer");
        result.DidYouMean.Count.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public void ExactName_ReturnsHitsAndEmptyDidYouMean()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find(
            "MaterializationService", null, null, SymbolLookup.DefaultMaxTokens, body: false);

        result.Matches.ShouldNotBeEmpty();
        result.DidYouMean.ShouldBeNull();
    }

    [Fact]
    public void DistantName_EmptyCandidateList()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find(
            "Xyzzy", null, null, SymbolLookup.DefaultMaxTokens, body: false);

        result.Matches.ShouldBeEmpty();
        result.DidYouMean.ShouldBeNull();
    }

    [Fact]
    public void KindFilter_MethodDoesNotSuggestTypes()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find(
            "MaterialzSvc", null, "method", SymbolLookup.DefaultMaxTokens, body: false);

        result.Matches.ShouldBeEmpty();
        if (result.DidYouMean is not null)
        {
            result.DidYouMean.ShouldNotContain("MaterializationService");
            result.DidYouMean.ShouldNotContain("Materializer");
        }
    }

    [Fact]
    public void DidYouMean_SmallBudget_Truncates()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find(
            "MaterializationServce", null, null, 15, body: false);

        result.Matches.ShouldBeEmpty();
        result.Truncated.ShouldBeTrue();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(15);
    }

    [Fact]
    public void DidYouMean_TieBreak_ScoreThenLengthThenOrdinal()
    {
        var workspace = NewIndexedWorkspaceWithTies();

        var result = new SymbolLookup(workspace).Find(
            "Abcdefghij", null, null, SymbolLookup.DefaultMaxTokens, body: false);

        result.Matches.ShouldBeEmpty();
        result.DidYouMean.ShouldNotBeNull();
        result.DidYouMean.Count.ShouldBe(3);
        var list = result.DidYouMean;
        for (var i = 0; i + 1 < list.Count; i++)
        {
            var scoreA = TrigramJaccard.Similarity("Abcdefghij", list[i]);
            var scoreB = TrigramJaccard.Similarity("Abcdefghij", list[i + 1]);
            if (Math.Abs(scoreA - scoreB) > 1e-9)
            {
                scoreA.ShouldBeGreaterThan(scoreB);
            }
            else if (list[i].Length != list[i + 1].Length)
            {
                list[i].Length.ShouldBeLessThan(list[i + 1].Length);
            }
            else
            {
                string.Compare(list[i], list[i + 1], StringComparison.Ordinal).ShouldBeLessThan(0);
            }
        }
    }

    [Fact]
    public async Task Cli_Symbol_NearName_RendersDidYouMeanLine()
    {
        var workspace = NewIndexedWorkspace();

        var output = await CliTestProcess.RunAsync(workspace, "symbol", "MaterializationServce");

        output.ExitCode.ShouldBe(0);
        output.StdOut.ShouldContain("did you mean:");
        output.StdOut.ShouldContain("Materializer");
    }

    [Fact]
    public async Task Cli_Symbol_DistantName_RendersNotFoundAndCodeSearchHint()
    {
        var workspace = NewIndexedWorkspace();

        var output = await CliTestProcess.RunAsync(workspace, "symbol", "Xyzzy");

        output.ExitCode.ShouldBe(0);
        output.StdOut.ShouldContain("No symbol named");
        output.StdOut.ShouldContain("use code_search for paraphrase queries");
    }

    [Fact]
    public async Task Cli_Symbol_Json_RendersDidYouMeanField()
    {
        var workspace = NewIndexedWorkspace();

        var output = await CliTestProcess.RunAsync(workspace, "symbol", "MaterializationServce", "--json");

        output.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(output.StdOut.Trim());
        var didYouMean = doc.RootElement.GetProperty("didYouMean");
        didYouMean.GetArrayLength().ShouldBeGreaterThan(0);
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/MaterializationService.cs", """
            namespace Demo;

            public class MaterializationService
            {
                public string Run() => "ok";
            }
            """);
        WriteFile(workspace, "repo/src/Materializer.cs", """
            namespace Demo;

            public class Materializer
            {
                public string Materialize() => "done";
            }
            """);
        WriteFile(workspace, "repo/src/SkillMaterializer.cs", """
            namespace Demo;

            public class SkillMaterializer
            {
                public string Run() => "skill";
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go() => 42;
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string NewIndexedWorkspaceWithTies()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/A.cs", """
            namespace Demo;
            public class AbcdefghXX { public void P() {} }
            """);
        WriteFile(workspace, "repo/src/B.cs", """
            namespace Demo;
            public class AbcdefghXXX { public void P() {} }
            """);
        WriteFile(workspace, "repo/src/C.cs", """
            namespace Demo;
            public class AbcdefghXXXX { public void P() {} }
            """);
        WriteFile(workspace, "repo/src/D.cs", """
            namespace Demo;
            public class AbcdefghXXXXX { public void P() {} }
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
}
