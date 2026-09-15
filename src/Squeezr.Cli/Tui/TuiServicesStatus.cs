using Spectre.Console;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiServicesStatus
{
    public static void Render(
        IAnsiConsole console,
        QdrantStatus qdrant,
        OllamaStatus ollama,
        string provider,
        string ollamaModel)
    {
        console.MarkupLine($"Embeddings pending: {qdrant.PendingChunks}");
        console.MarkupLine(qdrant.Available
            ? $"Qdrant: available ({qdrant.Points} points)"
            : "Qdrant: not available");
        if (!qdrant.Available)
        {
            console.MarkupLine("qdrant is down — start it with: squeezr services up");
        }

        if (IsOllamaProvider(provider))
        {
            if (ollama.Available)
            {
                console.MarkupLine(ollama.ModelInstalled
                    ? $"Ollama: available (model '{ollamaModel.EscapeMarkup()}' installed)"
                    : $"Ollama: available (model '{ollamaModel.EscapeMarkup()}' not installed — it will be pulled on the next index run)");
            }
            else
            {
                console.MarkupLine("Ollama: not available");
                console.MarkupLine("ollama is down — start it with: squeezr services up");
            }

            return;
        }

        console.MarkupLine(ollama.Available
            ? $"Ollama: running (not required — embeddings provider is '{provider.EscapeMarkup()}')"
            : $"Ollama: not running (not required — embeddings provider is '{provider.EscapeMarkup()}')");
    }

    private static bool IsOllamaProvider(string provider) =>
        string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase);
}
