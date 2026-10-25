using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;

namespace Squeezr.Cli.Embeddings;

public static class OllamaExecutorProbe
{
    private const string PsPath = "api/ps";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static async Task<EmbeddingExecutor> ProbeAsync(
        OllamaOptions options,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        http.Timeout = ProbeTimeout;

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(PsPath, ct);
        }
        catch (HttpRequestException ex)
        {
            return new EmbeddingExecutor("unknown", $"ollama /api/ps unreachable: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EmbeddingExecutor("unknown", "ollama /api/ps probe timed out");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new EmbeddingExecutor("unknown", $"ollama /api/ps returned HTTP {(int)response.StatusCode}");
            }

            PsResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<PsResponse>(ct);
            }
            catch (JsonException ex)
            {
                return new EmbeddingExecutor("unknown", $"ollama /api/ps payload is malformed: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new EmbeddingExecutor("unknown", "ollama /api/ps probe timed out");
            }

            var entry = payload?.Models.Find(e =>
                string.Equals(e.Name, options.Model, StringComparison.Ordinal)
                || e.Name.StartsWith(options.Model + ":", StringComparison.Ordinal));
            return entry is null
                ? new EmbeddingExecutor("unknown", $"model '{options.Model}' is not loaded, ollama /api/ps lists no such model")
                : DeriveFromEntry(entry);
        }
    }

    private static EmbeddingExecutor DeriveFromEntry(PsEntry entry)
    {
        var processor = entry.Processor;
        if (!string.IsNullOrWhiteSpace(processor))
        {
            var normalized = processor.ToLowerInvariant();
            if (normalized.Contains("gpu") && normalized.Contains("cpu"))
            {
                return new EmbeddingExecutor("unknown", $"model is split across cpu and gpu: '{processor}'");
            }

            if (normalized.Contains("gpu"))
            {
                return new EmbeddingExecutor("gpu", null);
            }

            if (normalized.Contains("cpu"))
            {
                return new EmbeddingExecutor("cpu", null);
            }
        }

        var size = entry.Size ?? 0;
        var sizeVram = entry.SizeVram ?? 0;
        if (size <= 0)
        {
            return new EmbeddingExecutor("unknown", "ollama /api/ps carries no usable size fields");
        }

        if (sizeVram > size * 0.99)
        {
            return new EmbeddingExecutor("gpu", null);
        }

        if (sizeVram <= 0)
        {
            return new EmbeddingExecutor("cpu", null);
        }

        var resident = Math.Round(100.0 * sizeVram / size);
        return new EmbeddingExecutor("unknown", $"{resident}% of the model resides in vram");
    }

    private sealed class PsResponse
    {
        [JsonPropertyName("models")]
        public List<PsEntry> Models { get; init; } = [];
    }

    private sealed class PsEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("processor")]
        public string? Processor { get; init; }

        [JsonPropertyName("size")]
        public double? Size { get; init; }

        [JsonPropertyName("size_vram")]
        public double? SizeVram { get; init; }
    }
}
