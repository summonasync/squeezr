using System.Net.Http;

namespace Squeezr.Semantic.Embeddings;

public sealed record LegacyReapReport(
    bool Ran,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> Kept,
    string? SkipReason = null);

// the auto-drop half of the 078 migration: after a successful reconcile against the owned
// instance, the squeezr-named collections on the old endpoint are removed — deny-by-default,
// a collection is touched only when its name is exactly a registered store key
public static class LegacyCollectionReaper
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task<LegacyReapReport> DropAsync(
        string legacyRestEndpoint,
        IReadOnlyCollection<string> allowedCollectionNames,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = ProbeTimeout;
        var baseUri = legacyRestEndpoint.TrimEnd('/') + "/";

        List<string> existing;
        try
        {
            existing = await QdrantCollectionListing.NamesAsync(http, baseUri, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LegacyReapReport(false, [], [], $"legacy endpoint unreachable: {legacyRestEndpoint}");
        }

        var allowed = allowedCollectionNames.ToHashSet(StringComparer.Ordinal);
        var targets = existing.Where(allowed.Contains).ToList();
        if (targets.Count == 0)
        {
            return new LegacyReapReport(true, [], existing, "no squeezr-named collections on the legacy endpoint");
        }

        var dropped = new List<string>();
        var kept = new List<string>();
        foreach (var name in targets)
        {
            try
            {
                using var response = await http.DeleteAsync(
                    baseUri + "collections/" + Uri.EscapeDataString(name), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    dropped.Add(name);
                }
                else
                {
                    kept.Add(name);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                kept.Add(name);
            }
        }

        return new LegacyReapReport(true, dropped, kept);
    }
}
