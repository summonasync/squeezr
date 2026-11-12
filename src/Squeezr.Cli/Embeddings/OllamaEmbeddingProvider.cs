using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;

namespace Squeezr.Cli.Embeddings;

public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IEmbeddingExecutorEvidence
{
    private const int MaxAttempts = 3;
    private const int BaseDelayMs = 200;
    private const int MaxBatchSize = 32;

    private readonly OllamaOptions _options;
    private readonly HttpMessageHandler? _handler;
    private readonly HttpClient _http;

    public int Dimensions => _options.Dimensions;

    public string ModelKey { get; }

    public OllamaEmbeddingProvider(OllamaOptions options, string modelKey, HttpMessageHandler? handler = null)
    {
        _options = options;
        ModelKey = modelKey;
        _handler = handler;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
    }

    public EmbeddingExecutor ProbeExecutor() =>
        OllamaExecutorProbe.ProbeAsync(_options, CancellationToken.None, _handler).GetAwaiter().GetResult();

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage = EmbeddingUsage.Document, CancellationToken ct = default)
    {
        var prefix = _options.ResolvedProfile?.PrefixFor(usage);
        var inputs = prefix is null ? texts : texts.Select(text => prefix + text).ToList();
        var results = new float[texts.Count][];

        for (var batchStart = 0; batchStart < texts.Count; batchStart += MaxBatchSize)
        {
            var batchCount = Math.Min(MaxBatchSize, texts.Count - batchStart);
            var batch = inputs.Skip(batchStart).Take(batchCount).ToList();
            var vectors = await EmbedBatchWithRetries(batch, ct);
            if (vectors.Length != batchCount)
                throw new InvalidOperationException($"Ollama returned {vectors.Length} embeddings for a batch of {batchCount} inputs.");
            for (var i = 0; i < batchCount; i++)
                results[batchStart + i] = vectors[i];
        }

        return results;
    }

    private async Task<float[][]> EmbedBatchWithRetries(IReadOnlyList<string> batch, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var payload = new EmbeddingRequest(batch, _options.Model);
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync("api/embed", payload, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts - 1)
            {
                await DelayBeforeRetry(attempt, ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts - 1)
            {
                await DelayBeforeRetry(attempt, ct);
                continue;
            }
            catch (HttpRequestException)
            {
                throw new HttpRequestException(OllamaReachability.NotReachableMessage(_options.Endpoint));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException(OllamaReachability.NotReachableMessage(_options.Endpoint));
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
                    if (body?.Embeddings is null || body.Embeddings.Count == 0)
                        throw new InvalidOperationException("Ollama /api/embed returned an empty 'embeddings' array.");
                    return body.Embeddings.ToArray();
                }

                var statusCode = (int)response.StatusCode;
                var detail = await response.Content.ReadAsStringAsync(ct);
                if ((statusCode == 429 || statusCode >= 500) && attempt < MaxAttempts - 1)
                {
                    await DelayBeforeRetry(attempt, ct);
                    continue;
                }

                throw new HttpRequestException($"Ollama /api/embed request failed with status {statusCode}. {detail}");
            }
        }
    }

    private static Task DelayBeforeRetry(int attempt, CancellationToken ct)
        => Task.Delay(BaseDelayMs * (1 << attempt), ct);

    private sealed record EmbeddingRequest(IReadOnlyList<string> Input, string Model);

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; init; } = [];
    }
}
