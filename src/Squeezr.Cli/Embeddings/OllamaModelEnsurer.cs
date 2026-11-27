using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Cli.Embeddings;

public static class OllamaModelEnsurer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Ensure(OllamaOptions options, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");

        var tags = GetTags(http, options);
        if (HasModel(tags, options.Model))
            return;

        Console.Error.WriteLine($"ollama model '{options.Model}' is not pulled locally; pulling from registry...");
        PullModel(http, options);
    }

    private static TagsResponse GetTags(HttpClient http, OllamaOptions options)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        HttpResponseMessage response;
        try
        {
            response = http.Send(request);
        }
        catch (HttpRequestException)
        {
            throw new WorkspaceException(OllamaReachability.NotReachableMessage(options.Endpoint));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                using var detailReader = new StreamReader(response.Content.ReadAsStream());
                var detail = detailReader.ReadToEnd();
                throw new WorkspaceException(
                    $"Ollama at '{options.Endpoint}' returned status {(int)response.StatusCode} when listing models. {detail}");
            }

            var body = response.Content.ReadAsStream();
            var tags = JsonSerializer.Deserialize<TagsResponse>(body, JsonOptions);
            return tags ?? new TagsResponse();
        }
    }

    private static bool HasModel(TagsResponse tags, string model)
    {
        foreach (var entry in tags.Models)
        {
            if (string.Equals(entry.Name, model, StringComparison.Ordinal)
                || entry.Name.StartsWith(model + ":", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void PullModel(HttpClient http, OllamaOptions options)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new PullRequest(options.Model)),
        };
        HttpResponseMessage response;
        try
        {
            response = http.Send(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new WorkspaceException(OllamaReachability.NotReachableWhilePullingMessage(options.Endpoint, options.Model));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                using var detailReader = new StreamReader(response.Content.ReadAsStream());
                var detail = detailReader.ReadToEnd();
                throw new WorkspaceException(
                    $"Failed to pull ollama model '{options.Model}': {(int)response.StatusCode}. {detail}");
            }

            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            _ = reader.ReadToEnd();
        }
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

    private sealed record PullRequest(string Name);
}
