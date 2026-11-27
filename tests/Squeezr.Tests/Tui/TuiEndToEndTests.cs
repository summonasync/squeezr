using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Tui;

public class TuiEndToEndTests
{
    [Fact]
    public async Task FullCircle_InitOffer_Add_Index_Dashboard_Search_Map_Config_Exit()
    {
        var workspace = TestDirs.NewTempDir();
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

        var console = TuiTestHarness.InteractiveConsole();

        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        TuiTestHarness.PushMenuChoice(console, 10);

        (await TuiApp.RunAsync(console, workspace)).ShouldBe(0);

        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);

        TuiTestHarness.PushMenuChoice(console, 4);
        console.Input.PushTextWithEnter("repo");
        console.Input.PushKey(ConsoleKey.Enter);

        TuiTestHarness.PushMenuChoice(console, 3);
        TuiTestHarness.PushMenuChoice(console, 0);

        TuiTestHarness.PushMenuChoice(console, 0);

        TuiTestHarness.PushMenuChoice(console, 1);
        console.Input.PushTextWithEnter("zanzibar");
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 2);

        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);

        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter("http://localhost:9999");
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);

        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("No workspace found here. Initialize now?");
        console.Output.ShouldContain("Initialized workspace");
        console.Output.ShouldContain("Added repo");
        console.Output.ShouldContain("1 files");
        console.Output.ShouldContain("Calc.cs:7");
        console.Output.ShouldContain("public int Add(int a, int b)");
        console.Output.ShouldContain("Calc.Add");
        SemanticConfigFile.Load(workspace).Qdrant.Endpoint.ShouldBe("http://localhost:9999");
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
