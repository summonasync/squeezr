using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class OllamaStatusProbe
{
    private const string TagsPath = "api/tags";

    public static async Task<OllamaStatus> ProbeAsync(
        OllamaOptions options,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
        => await ProbeAsync(options, OllamaProbe.Budget, ct, handler);

    internal static async Task<OllamaStatus> ProbeAsync(
        OllamaOptions options,
        TimeSpan budget,
        CancellationToken ct,
        HttpMessageHandler? handler)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(budget);

        TagsResponse? tags;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OllamaProbe.ProbeUri(options.Endpoint, TagsPath));
            using var response = await http.SendAsync(request, probeCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaStatus(false, false);
            }

            tags = await response.Content.ReadFromJsonAsync<TagsResponse>(probeCts.Token);
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
