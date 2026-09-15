using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiConfigEditorTests
{
    [Fact]
    public async Task Config_QdrantEndpointChange_Persists()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter("http://localhost:9999");
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        ConfigFile.Load(workspace).Qdrant.Endpoint.ShouldBe("http://localhost:9999");
        console.Output.ShouldContain("Saved.");
    }

    [Fact]
    public async Task Config_InvalidEndpoint_RetriesAndFileUntouched()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter("not a url");
        console.Input.PushTextWithEnter("");
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("invalid value");
        ConfigFile.Load(workspace).Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public async Task Config_EmptyInput_KeepsValue()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter("");
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("No changes.");
        ConfigFile.Load(workspace).Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public async Task Config_ProviderChange_SavesAndWarnsReembed()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 1);
        console.Input.PushTextWithEnter("openai");
        for (var i = 0; i < 9; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("reembed");
        ConfigFile.Load(workspace).Embeddings.Provider.ShouldBe("openai");
    }

    [Fact]
    public async Task Config_DiscardOnConfirm_LeavesFileUntouched()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 0);
        console.Input.PushTextWithEnter("http://localhost:7777");
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        ConfigFile.Load(workspace).Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public async Task Config_NonWorkspace_ShowsInitFirst()
    {
        var console = TuiTestHarness.InteractiveConsole();

        await TuiConfigEditor.RunAsync(console, TestDirs.NewTempDir(), CancellationToken.None);

        console.Output.ShouldContain("not initialized");
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");
        return workspace;
    }
}
