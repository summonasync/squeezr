using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class OllamaStatusProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static async Task<OllamaStatus> ProbeAsync(
        OllamaOptions options,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        http.Timeout = ProbeTimeout;

        TagsResponse? tags;
        try
        {
            tags = await http.GetFromJsonAsync<TagsResponse>("api/tags", ct);
        }
        catch (HttpRequestException)
        {
            return new OllamaStatus(false, false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new OllamaStatus(false, false);
        }
        catch (JsonException)
        {
            return new OllamaStatus(true, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (tags is null)
        {
            return new OllamaStatus(false, false);
        }

        foreach (var entry in tags.Models)
        {
            if (string.Equals(entry.Name, options.Model, StringComparison.Ordinal)
                || entry.Name.StartsWith(options.Model + ":", StringComparison.Ordinal))
            {
                return new OllamaStatus(true, true);
            }
        }

        return new OllamaStatus(true, false);
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagEntry> Models { get; init; } = [];

        public sealed class TagEntry
        {
            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;
        }
    }
}
