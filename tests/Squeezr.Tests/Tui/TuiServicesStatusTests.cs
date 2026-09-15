using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public class TuiServicesStatusTests
{
    [Fact]
    public void Render_QdrantDown_ShowsComposeHint()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(false, null, 2));

        console.Output.ShouldContain("Qdrant: not available");
        console.Output.ShouldContain("qdrant is down — start it with: docker compose up -d");
    }

    [Fact]
    public void Render_QdrantUp_OmitsHint()
    {
        var console = new TestConsole();

        TuiServicesStatus.Render(console, new QdrantStatus(true, 3, 0));

        console.Output.ShouldContain("Qdrant: available (3 points)");
        console.Output.ShouldNotContain("docker compose up -d");
    }
}
