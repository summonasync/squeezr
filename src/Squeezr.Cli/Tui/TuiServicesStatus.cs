using Spectre.Console;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiServicesStatus
{
    public static void Render(IAnsiConsole console, QdrantStatus qdrant)
    {
        console.MarkupLine($"Embeddings pending: {qdrant.PendingChunks}");
        console.MarkupLine(qdrant.Available
            ? $"Qdrant: available ({qdrant.Points} points)"
            : "Qdrant: not available");
        if (!qdrant.Available)
        {
            console.MarkupLine("qdrant is down — start it with: docker compose up -d");
        }
    }
}
