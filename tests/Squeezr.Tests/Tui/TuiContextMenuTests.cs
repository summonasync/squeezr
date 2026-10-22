using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiContextMenuTests
{
    [Fact]
    public async Task Entry_Uninitialized_AcceptedOffer_RunsInitFlow()
    {
        var workspace = TestDirs.NewTempDir();
        var console = TuiTestHarness.InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter(string.Empty);
        TuiTestHarness.PushMenuChoice(console, 9);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        WorkspaceManifestFile.Exists(workspace).ShouldBeTrue();
        console.Output.ShouldContain("No workspace found here. Initialize now?");
        console.Output.ShouldContain("Initialized workspace");
    }

    [Fact]
    public async Task Entry_Uninitialized_DeclinedOffer_ShowsInitializeMenu()
    {
        var workspace = TestDirs.NewTempDir();
        var console = TuiTestHarness.InteractiveConsole();
        console.Input.PushTextWithEnter("n");
        TuiTestHarness.PushMenuChoice(console, 1);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        WorkspaceManifestFile.Exists(workspace).ShouldBeFalse();
        console.Output.ShouldContain("No workspace found here. Initialize now?");
        console.Output.ShouldContain("Initialize workspace");
        console.Output.ShouldNotContain("Dashboard");
    }

    [Fact]
    public async Task UninitializedMenu_InitializeSelected_RunsInitThenShowsMainMenu()
    {
        var workspace = TestDirs.NewTempDir();
        var console = TuiTestHarness.InteractiveConsole();
        console.Input.PushTextWithEnter("n");
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter(string.Empty);
        TuiTestHarness.PushMenuChoice(console, 9);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        WorkspaceManifestFile.Exists(workspace).ShouldBeTrue();
        console.Output.ShouldContain("Initialized workspace");
        console.Output.ShouldContain("Add repository");
    }

    [Fact]
    public async Task MainMenu_Initialized_ContainsHumanNamedItemsWithoutWizards()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 9);

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
    public async Task Dashboard_ZeroRepositories_AcceptedOffer_RunsAddFlow()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        DisableQdrantProbe(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "repolib"));
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("repolib");
        console.Input.PushTextWithEnter(string.Empty);
        TuiTestHarness.PushMenuChoice(console, 9);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        var repos = WorkspaceManifestFile.Load(workspace).Repos;
        repos.ShouldHaveSingleItem().Name.ShouldBe("repolib");
        console.Output.ShouldContain("No repositories yet. Add one now?");
        console.Output.ShouldContain("Added repo");
    }

    [Fact]
    public async Task Dashboard_ZeroRepositories_DeclinedOffer_ReturnsToMainMenu()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        DisableQdrantProbe(workspace);
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter("n");
        TuiTestHarness.PushMenuChoice(console, 9);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        WorkspaceManifestFile.Load(workspace).Repos.ShouldBeEmpty();
        console.Output.ShouldContain("No repositories yet. Add one now?");
        console.Output.ShouldContain("Dashboard");
    }

    [Fact]
    public async Task Search_OutsideWorkspace_ShowsErrorAndAddRepositoryHint()
    {
        var console = TuiTestHarness.InteractiveConsole();

        await TuiSearchFlow.RunAsync(console, TestDirs.NewTempDir(), new TuiSession(), CancellationToken.None);

        console.Output.ShouldContain("Workspace not initialized.");
        console.Output.ShouldContain("add it via \"Add repository\" in the main menu");
    }

    [Fact]
    public async Task Map_OutsideWorkspace_ShowsErrorAndAddRepositoryHint()
    {
        var console = TuiTestHarness.InteractiveConsole();

        await TuiMapBrowser.RunAsync(console, TestDirs.NewTempDir(), CancellationToken.None);

        console.Output.ShouldContain("Workspace not initialized.");
        console.Output.ShouldContain("add it via \"Add repository\" in the main menu");
    }

    private static void DisableQdrantProbe(string workspace)
    {
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with { Qdrant = config.Qdrant with { Endpoint = "http://localhost:9" } });
    }
}
