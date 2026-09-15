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
        for (var i = 0; i < 13; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("reembed");
        console.Output.ShouldContain("incompatible");
        ConfigFile.Load(workspace).Embeddings.Provider.ShouldBe("openai");
    }

    [Fact]
    public async Task Config_OllamaProviderChange_SavesSection()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 1);
        console.Input.PushTextWithEnter("ollama");
        for (var i = 0; i < 13; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        var saved = ConfigFile.Load(workspace).Embeddings;
        saved.Provider.ShouldBe("ollama");
        saved.Ollama.Endpoint.ShouldBe("http://localhost:11434");
        saved.Ollama.Model.ShouldBe("nomic-embed-text");
        saved.Ollama.Dimensions.ShouldBe(768);
        saved.Ollama.ModelProfile.ShouldBe("nomic-embed-text-v1.5");
    }

    [Fact]
    public async Task Config_DimensionsChange_WarnsAndCancelLeavesFileUntouched()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 1);
        for (var i = 0; i < 3; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        console.Input.PushTextWithEnter("1536");
        for (var i = 0; i < 10; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("incompatible");
        console.Output.ShouldContain("reembed");
        console.Output.ShouldContain("Changes discarded.");
        ConfigFile.Load(workspace).Embeddings.Onnx.Dimensions.ShouldBeNull();
    }

    [Fact]
    public async Task Config_DimensionsChange_ApplySavesNewDimensions()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 1);
        for (var i = 0; i < 3; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        console.Input.PushTextWithEnter("1536");
        for (var i = 0; i < 10; i++)
        {
            console.Input.PushTextWithEnter("");
        }

        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("Saved.");
        ConfigFile.Load(workspace).Embeddings.Onnx.Dimensions.ShouldBe(1536);
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
    public async Task Config_OllamaModelProfileInvalid_RejectedAndFileUntouched()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 1);
        for (var i = 0; i < 13; i++)
        {
            console.Input.PushTextWithEnter(string.Empty);
        }

        console.Input.PushTextWithEnter("no-such-profile");
        console.Input.PushTextWithEnter(string.Empty);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("invalid value");
        console.Output.ShouldContain("unknown model profile 'no-such-profile'");
        ConfigFile.Load(workspace).Embeddings.Ollama.ModelProfile.ShouldBe("nomic-embed-text-v1.5");
    }

    [Fact]
    public async Task Config_OllamaModelProfileValidValueOrNull_Saves()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 5);
        TuiTestHarness.PushMenuChoice(console, 1);
        for (var i = 0; i < 13; i++)
        {
            console.Input.PushTextWithEnter(string.Empty);
        }

        console.Input.PushTextWithEnter("bge-code-v1");
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 4);
        TuiTestHarness.PushMenuChoice(console, 7);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("Saved.");
        ConfigFile.Load(workspace).Embeddings.Ollama.ModelProfile.ShouldBe("bge-code-v1");
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
