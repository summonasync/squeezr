using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiMapBrowserTests
{
    [Fact]
    public async Task Map_DrillDownToSymbol_ShowsSignature()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("[class]");
        console.Output.ShouldContain("[class] Calc");
        console.Output.ShouldContain("[method]");
        console.Output.ShouldContain("int Calc.Add(int a, int b)");
    }

    [Fact]
    public async Task Map_ExpandSymbol_ShowsBodySpan()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("src/Calc.cs:5-");
        console.Output.ShouldContain("return a + b;");
    }

    [Fact]
    public async Task Map_SubDir_BackClimbsToRepoListAndMenu()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("string Util.Shout(string word)");
    }

    [Fact]
    public async Task Map_NonWorkspace_ShowsNotInitialized()
    {
        var console = TuiTestHarness.InteractiveConsole();

        await TuiMapBrowser.RunAsync(console, TestDirs.NewTempDir(), CancellationToken.None);

        console.Output.ShouldContain("not initialized");
    }

    [Fact]
    public async Task Map_MissingIndex_ShowsRunIndexFirst()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        var console = TuiTestHarness.InteractiveConsole();

        await TuiMapBrowser.RunAsync(console, workspace, CancellationToken.None);

        console.Output.ShouldContain("Run 'squeezr index' first");
    }

    [Fact]
    public async Task Map_BracketFileName_RendersAndDrills()
    {
        var workspace = NewBracketWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("data[2024].cs");
        console.Output.ShouldContain("Data2024");
    }

    private static string NewBracketWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/data[2024].cs", """
            namespace Demo;

            public static class Data2024
            {
                public static int Answer()
                {
                    return 42;
                }
            }
            """);
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string NewIndexedWorkspace()
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
        WriteFile(workspace, "repo/src/Deep/Util.cs", """
            namespace Demo;

            public static class Util
            {
                public static string Shout(string word)
                {
                    return word.ToUpperInvariant();
                }
            }
            """);
        WriteFile(workspace, "repo/App.cs", """
            namespace Demo;

            public static class App
            {
                public static void Main()
                {
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
