using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Semantic.Embeddings;

public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider, IEmbeddingExecutorEvidence
{
    private const int MaxAttempts = 3;
    private const int BaseDelayMs = 200;

    private readonly OpenAiCompatibleOptions _options;
    private readonly HttpClient _http;

    public int Dimensions => _options.Dimensions;

    public string ModelKey { get; }

    public OpenAiCompatibleEmbeddingProvider(
        OpenAiCompatibleOptions options,
        string modelKey,
        HttpMessageHandler? handler = null)
    {
        _options = options;
        ModelKey = modelKey;
        var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"Environment variable '{options.ApiKeyEnv}' is not set. Export it with the API key for the embeddings endpoint '{options.Endpoint}'.");

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public EmbeddingExecutor ProbeExecutor() => new("remote", null);

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage = EmbeddingUsage.Document, CancellationToken ct = default)
    {
        var prefix = _options.ResolvedProfile?.PrefixFor(usage);
        var inputs = prefix is null ? texts : texts.Select(text => prefix + text).ToList();

        for (var attempt = 0; ; attempt++)
        {
            var payload = new EmbeddingRequest(inputs, _options.Model);
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync("embeddings", payload, ct);
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

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
                    if (body?.Data is null || body.Data.Count == 0)
                        throw new InvalidOperationException("Embeddings endpoint returned an empty 'data' array.");

                    var vectors = new float[texts.Count][];
                    foreach (var item in body.Data)
                        vectors[item.Index] = item.Embedding;
                    if (vectors.Any(v => v is null))
                        throw new InvalidOperationException("Embeddings endpoint response is missing vectors for some inputs.");
                    return vectors;
                }

                var statusCode = (int)response.StatusCode;
                var detail = await response.Content.ReadAsStringAsync(ct);
                if ((statusCode == 429 || statusCode >= 500) && attempt < MaxAttempts - 1)
                {
                    await DelayBeforeRetry(attempt, ct);
                    continue;
                }

                throw new HttpRequestException($"Embeddings request failed with status {statusCode}. {detail}");
            }
        }
    }

    private static Task DelayBeforeRetry(int attempt, CancellationToken ct)
        => Task.Delay(BaseDelayMs * (1 << attempt), ct);

    private sealed record EmbeddingRequest(IReadOnlyList<string> Input, string Model);

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingItem> Data { get; init; } = [];

        public sealed class EmbeddingItem
        {
            [JsonPropertyName("index")]
            public int Index { get; init; }

            [JsonPropertyName("embedding")]
            public required float[] Embedding { get; init; }
        }
    }
}
