using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Squeezr.Cli.Output;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Tui;

[Collection("ProcessGlobals")]
public class TuiIndexRunnerTests
{
    [Fact]
    public async Task Index_Incremental_CompletesAndShowsSummary()
    {
        var workspace = NewWorkspaceWithRepo();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 3);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("demo");
        console.Output.ShouldContain("+1");
        var repo = new WorkspaceManager(workspace).GetStatus().Repos.ShouldHaveSingleItem();
        repo.Files.ShouldBeGreaterThan(0);
        repo.Symbols.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Index_QdrantDown_CompletesWithWarning()
    {
        var workspace = NewWorkspaceWithRepo();
        var config = SemanticConfigFile.Load(workspace);
        SemanticConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with { Provider = "openai" },
            Qdrant = config.Qdrant with { Endpoint = "http://localhost:9" }
        });
        var previousKey = Environment.GetEnvironmentVariable("SQUEEZR_API_KEY");
        Environment.SetEnvironmentVariable("SQUEEZR_API_KEY", "test-key");
        try
        {
            var console = TuiTestHarness.InteractiveConsole();
            TuiTestHarness.PushMenuChoice(console, 3);
            TuiTestHarness.PushMenuChoice(console, 0);
            TuiTestHarness.PushMenuChoice(console, 10);

            var code = await TuiApp.RunAsync(console, workspace);

            code.ShouldBe(0);
            console.Output.ShouldContain("embeddings skipped");
            console.Output.ShouldContain("demo");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQUEEZR_API_KEY", previousKey);
        }
    }

    [Fact]
    public async Task Index_NoSemanticFromConfig_SkipsProviderResolution()
    {
        var workspace = NewWorkspaceWithRepo();
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(workspace);
        var config = SemanticConfigFile.Load(workspace);
        SemanticConfigFile.Save(workspace, config with { Indexing = config.Indexing with { NoSemantic = true } });

        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 3);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldNotContain("embeddings provider:");
    }

    [Fact]
    public async Task Index_SemanticEnabledWithDeadOllama_ReportsTheProbeFailure()
    {
        var workspace = NewWorkspaceWithRepo();
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(workspace);

        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 3);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldNotContain("embeddings provider:");
        console.Output.ShouldContain("ollama probe failed");
        console.Output.ShouldContain("'ollama' or 'openai'");
    }

    [Fact]
    public void Index_ProgressRenderer_RendersPhasesInTestConsole()
    {
        var workspace = NewWorkspaceWithRepo();
        var console = TuiTestHarness.InteractiveConsole();

        console.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(ctx => new WorkspaceIndexer().Index(
                workspace, progress: new IndexProgressRenderer(ctx)));

        console.Output.ShouldContain("files");
        console.Output.ShouldContain("semantic");
    }

    private static string NewWorkspaceWithRepo()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
