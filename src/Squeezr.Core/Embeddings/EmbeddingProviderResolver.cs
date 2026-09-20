using Squeezr.Core.Workspace;

namespace Squeezr.Core.Embeddings;

public sealed class EmbeddingProviderResolver
{
    private const string ProbePath = "api/tags";
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly TimeSpan _probeTimeout;

    public EmbeddingProviderResolver(HttpClient http)
        : this(http, DefaultProbeTimeout)
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
            case "onnx":
            case "ollama":
            case "openai":
                return new EmbeddingProviderResolution(normalized, null);
            case "auto":
                return await ResolveAutoAsync(ollamaEndpoint, cancellationToken);
            default:
                throw new WorkspaceException($"Unknown embeddings provider '{provider}'. Supported providers: onnx, ollama, openai, auto.");
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
                : FallbackToOnnx($"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FallbackToOnnx("probe timed out");
        }
        catch (HttpRequestException ex)
        {
            return FallbackToOnnx(ex.Message);
        }
    }

    private static EmbeddingProviderResolution FallbackToOnnx(string reason) => new("onnx", reason);

    private static Uri ProbeUri(string ollamaEndpoint)
        => new(ollamaEndpoint.TrimEnd('/') + "/" + ProbePath, UriKind.Absolute);
}
