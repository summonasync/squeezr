using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiSkeletonTests
{
    [Fact]
    public async Task Menu_RendersAndExit_ReturnsZero()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("Dashboard");
        console.Output.ShouldContain("Search");
        console.Output.ShouldContain("Map");
        console.Output.ShouldContain("Index");
        console.Output.ShouldContain("Add repository");
        console.Output.ShouldContain("Config");
        console.Output.ShouldContain("Drop workspace");
        console.Output.ShouldContain("Exit");
        console.Output.ShouldNotContain("Wizards");
    }

    [Fact]
    public async Task Dashboard_IndexedWorkspace_ShowsCounters()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("demo");
        console.Output.ShouldContain("1 files");
        console.Output.ShouldContain("symbols");
        console.Output.ShouldContain("chunks");
        console.Output.ShouldContain("Embeddings pending:");
        console.Output.ShouldContain("Qdrant: not available");
        console.Output.ShouldContain("Index age:");
    }

    [Fact]
    public async Task Dashboard_QdrantDown_ShowsStartHint()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();

        await TuiDashboard.RenderAsync(console, workspace);

        console.Output.ShouldContain("Qdrant: not available");
        console.Output.ShouldContain("qdrant is down — start it with: docker compose up -d");
    }

    [Fact]
    public async Task Dashboard_OutsideWorkspace_ShowsNotInitialized()
    {
        var console = TuiTestHarness.InteractiveConsole();

        await TuiDashboard.RenderAsync(console, TestDirs.NewTempDir());

        console.Output.ShouldContain("not initialized");
    }

    [Fact]
    public async Task BareRun_NonInteractive_GoesToHelpPath()
    {
        var console = new TestConsole();

        var code = await CliApplication.RunAsync([], console, interactive: false);

        code.ShouldBe(0);
        console.Output.ShouldNotContain("Coming in later tasks.");
    }

    [Fact]
    public async Task BareRun_Interactive_StartsTui()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var workspace = TestDirs.NewTempDir();
        Directory.SetCurrentDirectory(workspace);
        try
        {
            var console = TuiTestHarness.InteractiveConsole();
            console.Input.PushTextWithEnter("n");
            TuiTestHarness.PushMenuChoice(console, 1);

            var code = await CliApplication.RunAsync([], console, interactive: true);

            code.ShouldBe(0);
            console.Output.ShouldContain("No workspace found here. Initialize now?");
            console.Output.ShouldContain("Initialize workspace");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task Menu_PreCancelledToken_ReturnsZeroWithoutInput()
    {
        var console = TuiTestHarness.InteractiveConsole();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var code = await TuiApp.RunAsync(console, TestDirs.NewTempDir(), cts.Token);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Dashboard_MissingConfig_RendersWarning()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        File.Delete(SqueezrPaths.ConfigPath(workspace));
        var console = TuiTestHarness.InteractiveConsole();

        await TuiDashboard.RenderAsync(console, workspace);

        console.Output.ShouldContain("config file not found");
    }

    [Fact]
    public async Task Dashboard_BrokenQdrantConfig_RendersWarning()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with { Qdrant = config.Qdrant with { Endpoint = "not a uri" } });

        var console = TuiTestHarness.InteractiveConsole();
        await TuiDashboard.RenderAsync(console, workspace);

        console.Output.ShouldContain("Qdrant: not available");
        console.Output.ShouldContain("Invalid Qdrant endpoint");
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with { Qdrant = config.Qdrant with { Endpoint = "http://localhost:9" } });
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
