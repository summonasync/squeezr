using System.Text.Json;
using System.Text.RegularExpressions;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

internal static partial class WorkspaceCollectionSweep
{
    public const string ActionWouldReap = "would-reap";
    public const string ActionReaped = "reaped";
    public const string ActionReapFailed = "reap-failed";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    // The claim set is read from the registry here, at the moment the sweep asks, never handed in by the
    // caller: the row sweep runs first, so what this reads is what survived it. A collection is reap
    // candidate only when no surviving row names it, which is the direction that cannot take a live store's
    // vectors — the flipped order would delete a collection whose row had already gone but whose tree is
    // still indexed under another spelling.
    public static async Task<WorkspaceRepairCollectionsReport> RunAsync(
        IReadOnlyList<string> endpoints,
        bool apply,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        if (endpoints.Count == 0)
        {
            return new WorkspaceRepairCollectionsReport(false, apply, [], 0, [], [],
                "no workspace config named a Qdrant endpoint, so there was nothing to survey");
        }

        var claimed = ClaimedNames(WorkspaceRootResolver.GuardRegistryIo(WorkspaceRegistry.Load));
        var entries = new List<WorkspaceRepairCollectionEntry>();
        var unreachable = new List<string>();
        var seen = 0;

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = ProbeTimeout;

        foreach (var endpoint in endpoints)
        {
            List<string> observed;
            try
            {
                observed = await ListCollectionsAsync(http, endpoint, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        or IOException or UriFormatException or JsonException)
            {
                unreachable.Add(endpoint);
                continue;
            }

            var minted = observed
                .Where(IsMintedName)
                .Where(name => !claimed.Contains(name, StringComparer.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            seen += minted.Count;

            foreach (var name in minted)
            {
                if (!apply)
                {
                    entries.Add(new WorkspaceRepairCollectionEntry(endpoint, name, ActionWouldReap));
                    continue;
                }

                var failure = await TryDeleteCollectionAsync(http, endpoint, name, cancellationToken);
                entries.Add(new WorkspaceRepairCollectionEntry(
                    endpoint, name, failure is null ? ActionReaped : ActionReapFailed, failure));
            }
        }

        return new WorkspaceRepairCollectionsReport(
            unreachable.Count < endpoints.Count, apply, endpoints, seen, entries, unreachable);
    }

    // A store's collection is named by the spelling of its root, and one tree can survive as several
    // spellings: the row's own text, the final-component resolve, and the full link walk. All three are
    // claimed, because the process reading that store may arrive under any of them.
    public static IReadOnlyList<string> ClaimedNames(IReadOnlyList<WorkspaceRegistryEntry> rows)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var root in SpellingsOf(row.RootPath))
            {
                names.Add(QdrantVectorStore.GetCollectionName(root));
            }
        }

        return [.. names];
    }

    // Every endpoint this profile's live configs point at: a dead row's config is gone with its tree, so the
    // living rows are the only record of where this product's collections can be. A registry whose rows carry
    // no readable config names no endpoint, and the survey refuses instead of guessing one — a guessed shared
    // port would send a repair run to a Qdrant no row of this profile ever wrote to.
    public static IReadOnlyList<string> EndpointsOf(IReadOnlyList<WorkspaceRegistryEntry> rows)
    {
        var endpoints = new List<string>();
        foreach (var row in rows)
        {
            string endpoint;
            try
            {
                endpoint = ConfigFile.Load(row.RootPath).Qdrant.ResolvedEndpoint;
            }
            catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (endpoint.Length > 0 && !endpoints.Contains(endpoint, StringComparer.Ordinal))
            {
                endpoints.Add(endpoint);
            }
        }

        return endpoints;
    }

    public static bool IsMintedName(string collection) => MintedName().IsMatch(collection);

    private static IEnumerable<string> SpellingsOf(string rootPath)
    {
        yield return rootPath;
        yield return WorkspaceCanonicalPath.Canonicalize(rootPath);
        yield return WorkspaceCanonicalPath.ResolveThroughLinks(rootPath);
    }

    private static async Task<List<string>> ListCollectionsAsync(
        HttpClient http,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var baseUri = BaseUri(endpoint);
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

    private static async Task<string?> TryDeleteCollectionAsync(
        HttpClient http,
        string endpoint,
        string collection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.DeleteAsync(
                BaseUri(endpoint) + "collections/" + Uri.EscapeDataString(collection), cancellationToken);
            return response.IsSuccessStatusCode
                ? null
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string BaseUri(string endpoint) =>
        new Uri(endpoint).GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/";

    // The 078 naming is the only name this product writes; anything else on the endpoint belongs to
    // something that is not squeezr and is never a delete candidate, however stale it looks.
    [GeneratedRegex(@"^squeezr-[0-9a-f]{12}$")]
    private static partial Regex MintedName();
}
