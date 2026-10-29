using System.Text.RegularExpressions;
using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiWizardsTests
{
    [Fact]
    public async Task Init_CreatesStoreAndManifest()
    {
        var workspace = TestDirs.NewTempDir();
        var console = TuiTestHarness.InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter(string.Empty);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        Directory.Exists(SqueezrPaths.StoreDir(workspace)).ShouldBeTrue();
        WorkspaceManifestFile.Exists(workspace).ShouldBeTrue();
        var manifest = WorkspaceManifestFile.Load(workspace);
        manifest.Name.ShouldBe(new DirectoryInfo(workspace).Name);
        console.Output.ShouldContain("Initialized workspace");
        console.Output.ShouldContain("Workspace store:");
        console.Output.ShouldNotContain("squeezr.workspace.json");
        console.Output.ShouldNotContain("docker-compose");
    }

    [Fact]
    public async Task Init_AlreadyInitialized_ShowsMessageAndKeepsRunning()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var console = TuiTestHarness.InteractiveConsole();
        console.Input.PushTextWithEnter(string.Empty);

        await TuiWizards.InitAsync(console, workspace, CancellationToken.None);

        console.Output.ShouldContain("already initialized");
        WorkspaceManifestFile.Exists(workspace).ShouldBeTrue();
    }

    [Fact]
    public async Task Add_MissingPath_RepromptsThenAcceptsValidPath()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "repolib"));
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 4);
        console.Input.PushTextWithEnter("does-not-exist");
        console.Input.PushTextWithEnter("repolib");
        console.Input.PushTextWithEnter(string.Empty);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        var repos = WorkspaceManifestFile.Load(workspace).Repos;
        repos.ShouldHaveSingleItem().Name.ShouldBe("repolib");
        var flatOutput = Regex.Replace(console.Output, @"\s+", " ");
        flatOutput.ShouldContain("Added repo");
        flatOutput.ShouldContain("does not exist or is not a directory");
    }

    [Fact]
    public async Task Drop_No_KeepsEverything()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 6);
        console.Input.PushKey(ConsoleKey.Enter);
        TuiTestHarness.PushMenuChoice(console, 10);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        Directory.Exists(SqueezrPaths.StoreDir(workspace)).ShouldBeTrue();
        WorkspaceManifestFile.Exists(workspace).ShouldBeTrue();
        console.Output.ShouldContain("will be removed");
        console.Output.ShouldContain("Aborted. Nothing was removed.");
    }

    [Fact]
    public async Task Drop_Yes_RemovesStore()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 6);
        console.Input.PushTextWithEnter("y");
        TuiTestHarness.PushMenuChoice(console, 1);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        Directory.Exists(SqueezrPaths.StoreDir(workspace)).ShouldBeFalse();
        WorkspaceManifestFile.Exists(workspace).ShouldBeFalse();
        console.Output.ShouldContain("Removed");
        console.Output.ShouldContain("squeezr services down");
        console.Output.ShouldNotContain("docker compose");
        var flatOutput = Regex.Replace(console.Output, @"\s+", " ");
        flatOutput.ShouldContain("vector collection may still exist in the shared volume");
    }

    [Fact]
    public async Task Run_CancelledToken_AbortsWithoutSideEffects()
    {
        var workspace = TestDirs.NewTempDir();
        var console = TuiTestHarness.InteractiveConsole();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.NotThrowAsync(() => TuiApp.RunAsync(console, workspace, cts.Token));

        WorkspaceManifestFile.Exists(workspace).ShouldBeFalse();
        console.Output.ShouldNotContain("Initialized workspace");
    }
}
