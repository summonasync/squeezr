using Squeezr.Core.Workspace;

namespace Squeezr.Semantic.Embeddings;

public sealed class EmbeddingProviderResolver
{
    private const string ProbePath = "api/tags";

    private readonly HttpClient _http;
    private readonly TimeSpan _probeTimeout;

    public EmbeddingProviderResolver(HttpClient http)
        : this(http, OllamaProbe.Budget)
    {
    }

    internal EmbeddingProviderResolver(HttpClient http, TimeSpan probeTimeout)
    {
        _http = http;
        _probeTimeout = probeTimeout;
    }

    public async Task<EmbeddingProviderResolution> ResolveAsync(string provider, string ollamaEndpoint, CancellationToken cancellationToken)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "ollama":
            case "openai":
                return new EmbeddingProviderResolution(normalized, null);
            case "auto":
                return await ResolveAutoAsync(ollamaEndpoint, cancellationToken);
            default:
                throw new WorkspaceException($"Unknown embeddings provider '{provider}'. Supported providers: ollama, openai, auto.");
        }
    }

    private async Task<EmbeddingProviderResolution> ResolveAutoAsync(string ollamaEndpoint, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(_probeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProbeUri(ollamaEndpoint));
            using var response = await _http.SendAsync(request, probeCts.Token);
            return response.IsSuccessStatusCode
                ? new EmbeddingProviderResolution("ollama", null)
                : throw ProbeFailedException($"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProbeFailedException("probe timed out");
        }
        catch (HttpRequestException ex)
        {
            throw ProbeFailedException(ex.Message);
        }
    }

    private static WorkspaceException ProbeFailedException(string reason) => new(
        $"Ollama probe failed ({reason}). Set embeddings.provider to 'ollama' or 'openai' explicitly.");

    private static Uri ProbeUri(string ollamaEndpoint) => OllamaProbe.ProbeUri(ollamaEndpoint, ProbePath);
}
