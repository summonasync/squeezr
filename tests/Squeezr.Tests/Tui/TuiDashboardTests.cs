using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Tui;

public class TuiDashboardTests
{
    [Fact]
    public async Task Render_BothServicesDown_RendersBothSectionsWithServicesUpHints()
    {
        var workspace = NewWorkspaceWithDeadEndpoints();
        var console = new TestConsole();

        await TuiDashboard.RenderAsync(console, workspace);

        console.Output.ShouldContain("Qdrant: not available");
        console.Output.ShouldContain("qdrant is down — start it with: squeezr services up");
        console.Output.ShouldContain("Ollama: not running");
        console.Output.ShouldNotContain("docker compose");
    }

    [Fact]
    public async Task Render_ProviderOllama_OllamaDown_ShowsOllamaUnavailableAndHint()
    {
        var workspace = NewWorkspaceWithDeadEndpoints();
        PointConfigAt(workspace, provider: "ollama");
        var console = new TestConsole();

        await TuiDashboard.RenderAsync(console, workspace);

        console.Output.ShouldContain("Ollama: not available");
        console.Output.ShouldContain("ollama is down — start it with: squeezr services up");
    }

    [Fact]
    public async Task Render_UninitializedWorkspace_PromptsForInitWithoutProbing()
    {
        var console = new TestConsole();

        await TuiDashboard.RenderAsync(console, TestDirs.NewTempDir());

        console.Output.ShouldContain("Workspace not initialized");
    }

    private static string NewWorkspaceWithDeadEndpoints()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("dash");
        PointConfigAt(workspace, provider: "openai");
        return workspace;
    }

    private static void PointConfigAt(string workspace, string provider)
    {
        var config = SemanticConfigFile.Load(workspace) with
        {
            Qdrant = new QdrantConfig { Endpoint = "http://localhost:1" },
            Embeddings = new EmbeddingsConfig
            {
                Provider = provider,
                Ollama = new OllamaOptions { Endpoint = "http://localhost:1" },
            },
        };
        SemanticConfigFile.Save(workspace, config);
    }
}
