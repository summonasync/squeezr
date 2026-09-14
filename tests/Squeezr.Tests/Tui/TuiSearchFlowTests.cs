using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiSearchFlowTests
{
    [Fact]
    public async Task Search_KnownTerm_ShowsHitInList()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        console.Input.PushTextWithEnter("zanzibar");
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 6);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("demo/src/Calc.cs");
        console.Output.ShouldContain("zanzibar");
        console.Output.ShouldContain("[exact]");
    }

    [Fact]
    public async Task Search_SelectHit_ExpandsWithSurroundingLines()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        console.Input.PushTextWithEnter("zanzibar");
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 3);
        TuiTestHarness.PushMenuChoice(console, 6);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("demo/src/Calc.cs:7-7");
        console.Output.ShouldContain("public int Add(int a, int b)");
    }

    [Fact]
    public async Task Search_BackToHitsThenNewQuery_LoopsUntilMenu()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        console.Input.PushTextWithEnter("zanzibar");
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);
        console.Input.PushTextWithEnter("return");
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 6);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("var zanzibar = a + b;");
        console.Output.ShouldContain("return a + b;");
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsToMenu()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        console.Input.PushKey(ConsoleKey.Enter);
        TuiTestHarness.PushMenuChoice(console, 6);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("Search query");
    }

    [Fact]
    public async Task Search_QdrantDown_ShowsUnavailableHintOncePerSession()
    {
        TuiSearchFlow.ResetWarningDedup();
        var workspace = NewIndexedWorkspace();
        const string apiKeyEnv = "SQUEEZR_API_KEY_TUI_SEARCH_FLOW_TEST";
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with
            {
                Provider = "openai",
                OpenAiCompatible = config.Embeddings.OpenAiCompatible with { ApiKeyEnv = apiKeyEnv }
            },
            Qdrant = config.Qdrant with { Endpoint = "http://localhost:9" }
        });
        var previousKey = Environment.GetEnvironmentVariable(apiKeyEnv);
        Environment.SetEnvironmentVariable(apiKeyEnv, "test-key");
        try
        {
            var console = TuiTestHarness.InteractiveConsole();
            TuiTestHarness.PushMenuChoice(console, 2);
            console.Input.PushTextWithEnter("zanzibar");
            TuiTestHarness.PushMenuChoice(console, 2);
            TuiTestHarness.PushMenuChoice(console, 2);
            console.Input.PushTextWithEnter("return");
            TuiTestHarness.PushMenuChoice(console, 2);
            TuiTestHarness.PushMenuChoice(console, 6);

            var code = await TuiApp.RunAsync(console, workspace);

            code.ShouldBe(0);
            CountOccurrences(console.Output, "vector store is unavailable").ShouldBe(1);
            console.Output.ShouldNotContain("warning:");
            console.Output.ShouldContain("demo/src/Calc.cs");
            console.Output.ShouldContain("return a + b;");
        }
        finally
        {
            Environment.SetEnvironmentVariable(apiKeyEnv, previousKey);
        }
    }

    private static int CountOccurrences(string text, string substring) =>
        text.Split(substring, StringSplitOptions.None).Length - 1;

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b)
                {
                    var zanzibar = a + b;
                    return a + b;
                }
            }
            """);
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
