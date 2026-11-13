using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Health;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class EmbeddingsHealthProbe
{
    public static async Task<EmbeddingsHealth> ProbeAsync(
        string workspaceRoot,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        try
        {
            var config = ConfigFile.Load(workspaceRoot);
            if (config.Indexing.NoSemantic)
            {
                return EmbeddingsHealth.Silent;
            }

            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            var resolution = await new EmbeddingProviderResolver(http)
                .ResolveAsync(config.Embeddings.Provider, config.Embeddings.Ollama.Endpoint, ct);
            var cpu = resolution.Provider == "ollama"
                ? await CpuOnlyAsync(config.Embeddings.Ollama, ct, handler)
                : null;
            return new EmbeddingsHealth(
                resolution.Provider, null, cpu, ActiveModelKey(config.Embeddings, resolution.Provider));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex is WorkspaceException { Code: WorkspaceException.OnnxProviderCode }
                ? new EmbeddingsHealth(null, OnnxStoreDiagnostic.Text(), null)
                : EmbeddingsHealth.Silent;
        }
    }

    private static string? ActiveModelKey(EmbeddingsConfig config, string resolvedProvider)
    {
        try
        {
            return EmbeddingModelKey.From(config with { Provider = resolvedProvider });
        }
        catch (Exception ex) when (ex is WorkspaceException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> CpuOnlyAsync(OllamaOptions options, CancellationToken ct, HttpMessageHandler? handler)
    {
        var executor = await OllamaExecutorProbe.ProbeAsync(options, ct, handler);
        return executor.Device == "cpu"
            ? "embedding model runs on CPU — embeddings will be slow"
            : null;
    }
}
