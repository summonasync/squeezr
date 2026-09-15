namespace Squeezr.Cli.Embeddings;

internal static class OllamaReachability
{
    public static string NotReachableMessage(string endpoint)
        => $"Ollama is not reachable at {endpoint}. Start it with: squeezr services up";

    public static string NotReachableWhilePullingMessage(string endpoint, string model)
        => NotReachableMessage(endpoint) + $" (lost connection while pulling model '{model}'; retry once Ollama is back)";
}
