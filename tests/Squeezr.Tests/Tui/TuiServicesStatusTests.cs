using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiServicesStatusTests
{
    [Fact]
    public void Render_QdrantDown_ShowsServicesUpHint()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(false, null, 2), new OllamaStatus(true, true), "openai", "nomic-embed-text");

        console.Output.ShouldContain("Qdrant: not available");
        console.Output.ShouldContain("qdrant is down — start it with: squeezr services up");
        console.Output.ShouldNotContain("docker compose");
    }

    [Fact]
    public void Render_QdrantUp_OmitsHint()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0), new OllamaStatus(true, true), "openai", "nomic-embed-text");

        console.Output.ShouldContain("Qdrant: available (3 points)");
        console.Output.ShouldNotContain("squeezr services up");
    }

    [Fact]
    public void Render_OllamaDown_ProviderOllama_ShowsUnavailableAndHint()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0), new OllamaStatus(false, false), "ollama", "nomic-embed-text");

        console.Output.ShouldContain("Ollama: not available");
        console.Output.ShouldContain("ollama is down — start it with: squeezr services up");
    }

    [Fact]
    public void Render_OllamaUp_ModelNotInstalled_ProviderOllama_ShowsNotInstalledMarker()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0), new OllamaStatus(true, false), "ollama", "nomic-embed-text");

        console.Output.ShouldContain("Ollama: available");
        console.Output.ShouldContain("not installed");
        console.Output.ShouldContain("nomic-embed-text");
    }

    [Fact]
    public void Render_OllamaUp_ModelInstalled_ProviderOllama_ShowsInstalledMarker()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0), new OllamaStatus(true, true), "ollama", "nomic-embed-text");

        console.Output.ShouldContain("Ollama: available (model 'nomic-embed-text' installed)");
        console.Output.ShouldNotContain("not installed");
    }

    [Fact]
    public void Render_ProviderOpenAi_OllamaRunning_ShowsCompactSectionWithoutModelRequirement()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0), new OllamaStatus(true, false), "openai", "nomic-embed-text");

        console.Output.ShouldContain("Ollama: running");
        console.Output.ShouldContain("not required");
        console.Output.ShouldNotContain("not installed");
        console.Output.ShouldNotContain("squeezr services up");
    }

    [Fact]
    public void Render_ProviderOpenAi_OllamaNotRunning_ShowsCompactSectionWithoutHint()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0), new OllamaStatus(false, false), "openai", "nomic-embed-text");

        console.Output.ShouldContain("Ollama: not running");
        console.Output.ShouldContain("not required");
        console.Output.ShouldNotContain("squeezr services up");
    }
}
