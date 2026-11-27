using System.Text.Json;

namespace Squeezr.Semantic.Embeddings;

// One parse of the Qdrant collection listing, shared by the 078 legacy reaper and the repair sweep: same
// endpoint, same wire shape, so it is read the same way. Which failures count as unreachable stays the
// caller's policy — this helper just does not answer twice.
public static class QdrantCollectionListing
{
    public static async Task<List<string>> NamesAsync(
        HttpClient http,
        string baseUri,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(baseUri + "collections", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var names = new List<string>();
        if (document.RootElement.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("collections", out var collections) &&
            collections.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in collections.EnumerateArray())
            {
                if (entry.TryGetProperty("collection_name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    names.Add(name.GetString()!);
                }
            }
        }

        return names;
    }
}
