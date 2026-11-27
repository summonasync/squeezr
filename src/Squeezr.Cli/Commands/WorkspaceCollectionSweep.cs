using System.Text.Json;
using System.Text.RegularExpressions;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Cli.Commands;

internal static partial class WorkspaceCollectionSweep
{
    public const string ActionWouldReap = "would-reap";
    public const string ActionReaped = "reaped";
    public const string ActionReapFailed = "reap-failed";
    public const string ActionSkippedNotOwned = "skipped-not-owned";
    public const string ReasonNotOwned = "endpoint-not-owned";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    // The collection door opens only on a run that both carries --apply and finished its row sweep
    // confirmed: a preview, a refusal, a declined prompt or a sweep that died halfway has not earned a
    // delete on any endpoint. ModeNone counts as confirmed — a registry with nothing to prune is a row
    // phase that completed honestly, and --apply on that run is consent, not an accident.
    public static bool CollectionApplyGate(bool apply, string pruneMode) =>
        apply && pruneMode is WorkspaceRepairPruner.ModePruned or WorkspaceRepairPruner.ModeNone;

    // The claim set is read from the registry here, at the moment the sweep asks, never handed in by the
    // caller: the row sweep runs first, so what this reads is what survived it. A collection is reap
    // candidate only when no surviving row names it, which is the direction that cannot take a live store's
    // vectors — the flipped order would delete a collection whose row had already gone but whose tree is
    // still indexed under another spelling.
    // Deleting on an endpoint this profile does not call its own is not a repair, it is a raid on another
    // home's vectors: the shared standard port holds collections named by registries this run never read, and
    // a claim set built from live rows here cannot tell those from leaks. So the survey still walks a
    // non-owned endpoint — the ledger has to say what it saw — but every candidate on it lands as a counted
    // skip with a machine-readable reason, and the delete request is never even built. There is no consent
    // path around this gate: --apply on a non-owned endpoint means "delete on the endpoints I own", per the
    // owner ruling that closed F4.
    public static async Task<WorkspaceRepairCollectionsReport> RunAsync(
        IReadOnlyList<QdrantSweepTarget> targets,
        bool apply,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        if (targets.Count == 0)
        {
            return new WorkspaceRepairCollectionsReport(false, apply, [], 0, [], [],
                "no workspace config named a Qdrant endpoint, so there was nothing to survey");
        }

        var endpoints = targets.Select(target => target.Endpoint).ToList();
        var notOwned = targets.Where(target => !target.Owned).Select(target => target.Endpoint).ToList();
        var claimed = ClaimedNames(WorkspaceRootResolver.GuardRegistryIo(WorkspaceRegistry.Load));
        var entries = new List<WorkspaceRepairCollectionEntry>();
        var unreachable = new List<string>();
        var faults = new List<WorkspaceRepairEndpointFault>();
        var seen = 0;

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = ProbeTimeout;

        foreach (var target in targets)
        {
            List<string> observed;
            try
            {
                observed = await QdrantCollectionListing.NamesAsync(http, BaseUri(target.Endpoint), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        or IOException or UriFormatException or JsonException)
            {
                unreachable.Add(target.Endpoint);
                faults.Add(new WorkspaceRepairEndpointFault(target.Endpoint, DescribeFault(ex)));
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
                if (!target.Owned)
                {
                    entries.Add(new WorkspaceRepairCollectionEntry(
                        target.Endpoint, name, ActionSkippedNotOwned, null, ReasonNotOwned));
                    continue;
                }

                if (!apply)
                {
                    entries.Add(new WorkspaceRepairCollectionEntry(target.Endpoint, name, ActionWouldReap));
                    continue;
                }

                var failure = await TryDeleteCollectionAsync(http, target.Endpoint, name, cancellationToken);
                entries.Add(new WorkspaceRepairCollectionEntry(
                    target.Endpoint, name, failure is null ? ActionReaped : ActionReapFailed, failure));
            }
        }

        return new WorkspaceRepairCollectionsReport(
            unreachable.Count < targets.Count, apply, endpoints, seen, entries, unreachable,
            null, notOwned, faults);
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

    // Every endpoint this profile's live configs point at, each once, with the ownership flag read from the
    // config that named it: a dead row's config is gone with its tree, so the living rows are the only record
    // of where this product's collections can be, and which of them this run may delete on. A registry whose
    // rows carry no readable config names no endpoint, and the survey refuses instead of guessing one — a
    // guessed shared port would send a repair run to a Qdrant no row of this profile ever wrote to.
    public static IReadOnlyList<QdrantSweepTarget> SweepTargetsOf(IReadOnlyList<WorkspaceRegistryEntry> rows)
    {
        var targets = new List<QdrantSweepTarget>();
        foreach (var row in rows)
        {
            QdrantConfig qdrant;
            try
            {
                qdrant = SemanticConfigFile.Load(row.RootPath).Qdrant;
            }
            catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var endpoint = qdrant.ResolvedEndpoint;
            if (endpoint.Length == 0)
            {
                continue;
            }

            var at = targets.FindIndex(target =>
                string.Equals(target.Endpoint, endpoint, StringComparison.Ordinal));
            if (at < 0)
            {
                targets.Add(new QdrantSweepTarget(endpoint, qdrant.Owned));
            }
            else if (qdrant.Owned)
            {
                targets[at] = targets[at] with { Owned = true };
            }
        }

        return targets;
    }

    // What the ledger covers, ownership aside: the survey walks all of it and refuses to delete on the part
    // this profile does not own, so a skipped endpoint still shows up as a surveyed one.
    public static IReadOnlyList<string> EndpointsOf(IReadOnlyList<WorkspaceRegistryEntry> rows) =>
        [.. SweepTargetsOf(rows).Select(target => target.Endpoint)];

    public static bool IsMintedName(string collection) => MintedName().IsMatch(collection);

    private static IEnumerable<string> SpellingsOf(string rootPath)
    {
        yield return rootPath;
        yield return WorkspaceCanonicalPath.Canonicalize(rootPath);
        yield return WorkspaceCanonicalPath.ResolveThroughLinks(rootPath);
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

    // The error the sweep's own request caught, typed and verbatim: "no listener on this port" and "the
    // collection set here is empty" must never collapse into the same zero, and a rerouted guess about a
    // dead endpoint is worse than the failure it hides.
    private static string DescribeFault(Exception ex) =>
        ex.Message.Length > 0 ? $"{ex.GetType().Name}: {ex.Message}" : ex.GetType().Name;

    // The 078 naming is the only name this product writes; anything else on the endpoint belongs to
    // something that is not squeezr and is never a delete candidate, however stale it looks.
    [GeneratedRegex(@"^squeezr-[0-9a-f]{12}$")]
    private static partial Regex MintedName();
}
