using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// The collection arm is a conversation with a store this suite must never touch, so it is measured against a
// scripted endpoint: what the sweep asks for, in which order, and what it leaves alone. The Qdrant of this
// machine is not a test double and never a target.
[Collection("ProcessGlobals")]
public sealed class WorkspaceCollectionSweepTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string Endpoint = "http://qdrant.test:6334";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceCollectionSweepTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = Tracked(TestDirs.NewTempDir());
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        Directory.CreateDirectory(_home);
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_UnreferencedCollection_WithoutApply_ListsItAndDeletesNothing()
    {
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var server = FakeQdrant.With(Collection(goneRoot));

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: false, CancellationToken.None, server);

        report.Ran.ShouldBeTrue();
        report.Apply.ShouldBeFalse();
        report.SeenMinted.ShouldBe(1);
        report.Entries.ShouldHaveSingleItem().Action.ShouldBe(WorkspaceCollectionSweep.ActionWouldReap);
        server.Deleted.ShouldBeEmpty("a report is not consent");
    }

    [Fact]
    public async Task RunAsync_UnreferencedCollection_WithApply_DeletesItAndKeepsClaimedAndForeign()
    {
        var (claimedRoot, _, _) = WriteRow("live-lane");
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var server = FakeQdrant.With(
            Collection(claimedRoot), Collection(goneRoot), "squeezr-d23ab1c2", "grimoire-ae74daf9", "system");

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: true, CancellationToken.None, server);

        report.Entries.ShouldHaveSingleItem().Collection.ShouldBe(Collection(goneRoot));
        server.Deleted.ShouldBe([Collection(goneRoot)],
            "a name this product never minted, or one a live row still names, is not this sweep's to delete");
        report.Apply.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_AfterTheRowSweep_LiveChildProtectsItsCollection_DeadChildLosesHers()
    {
        var (parentRoot, parentKey, _) = WriteRow("main");
        var (liveChildRoot, liveKey, _) = WriteRow("lane-live");
        var (deadChildRoot, deadKey, deadStoreDir) = WriteRow("lane-dead");
        var server = FakeQdrant.With(
            Collection(parentRoot), Collection(liveChildRoot), Collection(deadChildRoot));

        await WorkspaceStoreDropper.DropAsync(deadKey, deadChildRoot, null, CancellationToken.None);

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: true, CancellationToken.None, server);

        var aliveAtListTime = server.KeysAtListTime.ShouldHaveSingleItem();
        aliveAtListTime.ShouldNotContain(deadKey,
            "the sweep reads the registry after the row sweep, so what it protects is what survived it");
        aliveAtListTime.ShouldContain(liveKey);
        server.Deleted.ShouldBe([Collection(deadChildRoot)]);
        report.Entries.ShouldHaveSingleItem().Action.ShouldBe(WorkspaceCollectionSweep.ActionReaped);
        Directory.Exists(deadStoreDir).ShouldBeFalse();
        WorkspaceRegistry.Load().Select(row => row.StoreKey).ShouldBe([parentKey, liveKey]);
    }

    [Fact]
    public async Task RunAsync_DeleteRefusedByEndpoint_ReportsFailure_AndTouchesNothingElse()
    {
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var server = FakeQdrant.With(Collection(goneRoot))
            .RefusingDeletesWith(HttpStatusCode.Conflict, "collection is aliased");

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: true, CancellationToken.None, server);

        var entry = report.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe(WorkspaceCollectionSweep.ActionReapFailed);
        entry.Failure.ShouldNotBeNullOrEmpty();
        report.Ran.ShouldBeTrue("the endpoint answered, so the survey is not a skip");
        report.SeenMinted.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_EndpointUnreachable_ReportsItAsUncovered_DeletesNothing()
    {
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var server = FakeQdrant.With(Collection(goneRoot)).Unreachable();

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: true, CancellationToken.None, server);

        report.Ran.ShouldBeFalse();
        report.Entries.ShouldBeEmpty();
        report.UnreachableEndpoints.ShouldBe([Endpoint]);
        report.Endpoints.ShouldBe([Endpoint], "the ledger names what it covered, even when nothing covered it");
        server.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_NoEndpointsNamed_SkipsWithAReason()
    {
        var report = await WorkspaceCollectionSweep.RunAsync([], apply: true, CancellationToken.None, FakeQdrant.With());

        report.Ran.ShouldBeFalse();
        report.SkipReason.ShouldNotBeNull().ShouldContain("no workspace config named a Qdrant endpoint");
    }

    // The F4 shape: a pinned scratch home whose rows carry the default config names the shared standard port,
    // and the collections living there were minted by a registry this run never read. Walking that endpoint is
    // what lets the ledger say what it saw; deleting on it is what the Owned flag refuses, and the refusal has
    // to be a counted line with a machine-readable reason — a would-reap row here would promise a delete that
    // no flag can ever authorize.
    [Fact]
    public async Task RunAsync_EndpointThisProfileDoesNotOwn_CountsTheSkipAndBuildsNoDeleteRequest()
    {
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var preview = FakeQdrant.With(Collection(goneRoot), "overmind_entries", "grimoire-ae74daf9");

        var reported = await WorkspaceCollectionSweep.RunAsync(
            [Foreign(Endpoint)], apply: false, CancellationToken.None, preview);

        reported.Ran.ShouldBeTrue("the survey walked it, so the run is not a skip of the whole arm");
        reported.SeenMinted.ShouldBe(1, "the foreign pair is not this product's name, so it is not a candidate");
        var listed = reported.Entries.ShouldHaveSingleItem();
        listed.Action.ShouldBe(WorkspaceCollectionSweep.ActionSkippedNotOwned);
        listed.Reason.ShouldBe(WorkspaceCollectionSweep.ReasonNotOwned);
        reported.NotOwnedEndpoints.ShouldBe([Endpoint]);
        preview.Requests.ShouldBe(["GET /collections"], "a refusal reads the ledger, it does not write it");

        var applied = FakeQdrant.With(Collection(goneRoot), "overmind_entries", "grimoire-ae74daf9");
        var appliedReport = await WorkspaceCollectionSweep.RunAsync(
            [Foreign(Endpoint)], apply: true, CancellationToken.None, applied);

        appliedReport.Apply.ShouldBeTrue("--apply is in hand, and it is still not consent on another home's port");
        appliedReport.Entries.ShouldHaveSingleItem().Action.ShouldBe(
            WorkspaceCollectionSweep.ActionSkippedNotOwned);
        applied.Deleted.ShouldBeEmpty();
        applied.Requests.ShouldBe(["GET /collections"],
            "no DELETE request is ever built for a non-owned endpoint, not even one that would be refused");
    }

    [Fact]
    public async Task RunAsync_OwnedAndForeignEndpointsNamingTheSameCollection_ReapHereAndHoldThere()
    {
        const string foreignEndpoint = "http://foreign.test:6333";
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var server = FakeQdrant.With(Collection(goneRoot));

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint), Foreign(foreignEndpoint)], apply: true, CancellationToken.None, server);

        report.Endpoints.ShouldBe([Endpoint, foreignEndpoint]);
        report.SeenMinted.ShouldBe(2, "one name seen on each of the two endpoints that hold it");
        report.Entries.Select(entry => entry.Action).ShouldBe(
            [WorkspaceCollectionSweep.ActionReaped, WorkspaceCollectionSweep.ActionSkippedNotOwned]);
        report.Entries.Select(entry => entry.Endpoint).ShouldBe([Endpoint, foreignEndpoint]);
        report.NotOwnedEndpoints.ShouldBe([foreignEndpoint],
            "the owned half did not lend its flag to the sibling and the foreign half did not strip it");
        server.Deleted.ShouldBe([Collection(goneRoot)],
            "one collection seen twice is deleted once, on the endpoint that declared itself mine");
    }

    // The dead-port class the marshal measured: two live profiles name a port nothing listens on. The sweep
    // may not read that as an empty collection set, may not send the survey to a default port instead, and has
    // to report the error its own request caught so a reader can tell the two apart.
    [Fact]
    public async Task RunAsync_OwnedEndpointWithNoListener_ReportsItsMeasuredFault_NotAQuietZero()
    {
        var (goneRoot, _, _) = WriteRow("gone-lane");
        RemoveRow("gone-lane");
        var server = FakeQdrant.With(Collection(goneRoot)).Unreachable();

        var report = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: true, CancellationToken.None, server);

        report.Ran.ShouldBeFalse();
        report.SeenMinted.ShouldBe(0);
        report.UnreachableEndpoints.ShouldBe([Endpoint]);
        var fault = report.EndpointFaults.ShouldNotBeNull().ShouldHaveSingleItem();
        fault.Endpoint.ShouldBe(Endpoint);
        fault.Error.ShouldBe("HttpRequestException: connection refused",
            "the fault line is the exception this run actually caught, typed and passed through");
        report.NotOwnedEndpoints.ShouldBeEmpty();
        server.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public void SweepTargetsOf_TwoConfigs_TwoEndpoints_ClassifiesEachFromItsOwnFile()
    {
        var ownedHome = Tracked(TestDirs.NewTempDir());
        var foreignHome = Tracked(TestDirs.NewTempDir());
        var sharedHome = Tracked(TestDirs.NewTempDir());
        var bare = Tracked(TestDirs.NewTempDir());
        WriteConfig(ownedHome, "http://one.test:6334", owned: true);
        WriteConfig(foreignHome, "http://one.test:6334");
        WriteConfig(sharedHome, "http://two.test:6333", owned: true);
        var rows = new[]
        {
            RowOf("foreign-first", foreignHome),
            RowOf("mine", ownedHome),
            RowOf("also-mine", sharedHome),
            RowOf("bare", bare),
        };

        var targets = WorkspaceCollectionSweep.SweepTargetsOf(rows);

        targets.ShouldBe(
            [
                new QdrantSweepTarget("http://one.test:6334", true),
                new QdrantSweepTarget("http://two.test:6333", true),
            ],
            "one endpoint, one target: the row that declared it its own wins the flag for the shared spelling, " +
            "and an owned config naming an explicit endpoint resolves there instead of deriving a port");
        WorkspaceCollectionSweep.EndpointsOf(rows).ShouldBe(["http://one.test:6334", "http://two.test:6333"],
            "the unowned projection the ledger reports is unchanged by the gate — every named endpoint is " +
            "still surveyed, ownership only decides what may be deleted on it");

        // the strip direction of the same promise: an unowned sibling arriving AFTER the declaring row is
        // not a veto over it either — ownership must survive being second in file order, not just first
        var rowsReversed = new[]
        {
            RowOf("mine", ownedHome),
            RowOf("foreign-first", foreignHome),
            RowOf("also-mine", sharedHome),
            RowOf("bare", bare),
        };

        WorkspaceCollectionSweep.SweepTargetsOf(rowsReversed).ShouldBe(
            [
                new QdrantSweepTarget("http://one.test:6334", true),
                new QdrantSweepTarget("http://two.test:6333", true),
            ],
            "row order decides nothing: the declaring row wins the flag from ahead and from behind alike");
    }

    // Forward-only, exactly as the claim set is built: a name derived from a surviving row is claimed and a
    // name that merely looks like one is not. Backward reconstruction from the endpoint's inventory is dead —
    // the measured inventory holds hundreds of names no registry anywhere answers for — so nothing in this
    // predicate may claim to know where a name came from.
    [Fact]
    public void ClaimedNames_SurvivingRows_AnswerForwardForTheirOwnNamesOnly()
    {
        var (liveRoot, _, _) = WriteRow("live-lane");
        var goneRoot = Tracked(TestDirs.NewTempDir());
        var rows = WorkspaceRegistry.Load();

        var claimed = WorkspaceCollectionSweep.ClaimedNames(rows);

        claimed.ShouldContain(Collection(liveRoot),
            "a live row names its own collection, in each spelling its serve path can open");
        claimed.ShouldNotContain(Collection(goneRoot),
            "no row of this registry derives that name, so it is unclaimed — which is the whole of what this " +
            "sweep is allowed to say about where it came from");
        claimed.ShouldNotContain("overmind_entries");
        claimed.ShouldNotContain("grimoire-ae74daf9");
        claimed.Count.ShouldBe(claimed.Distinct().Count(), "the claim set is a set");
    }

    // Dry-run output is the only legal source of a deletion set: the same candidate list, read from the same
    // registry at the same point in the run, decides both what is listed and what is deleted. Measured
    // behaviorally — two scripted endpoints seeded alike, preview first, apply second.
    [Fact]
    public async Task RunAsync_ApplyPathDeletesExactlyWhatTheDryRunListed()
    {
        var (firstRoot, _, _) = WriteRow("gone-one");
        var (secondRoot, _, _) = WriteRow("gone-two");
        RemoveRow("gone-one");
        RemoveRow("gone-two");
        var seeded = new[]
        {
            Collection(firstRoot), Collection(secondRoot), "squeezr-0a1b2c3d4e5f", "overmind_entries",
        };

        var previewed = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: false, CancellationToken.None, FakeQdrant.With(seeded));
        var server = FakeQdrant.With(seeded);
        var applied = await WorkspaceCollectionSweep.RunAsync(
            [Owned(Endpoint)], apply: true, CancellationToken.None, server);

        previewed.Entries.ShouldAllBe(entry => entry.Action == WorkspaceCollectionSweep.ActionWouldReap);
        var listed = previewed.Entries.Select(entry => entry.Collection).ToList();
        listed.ShouldNotContain(name => name == "overmind_entries",
            "a name this product never minted is not on the list the apply path may work, in either mode");
        listed.ShouldBe(
            new[] { Collection(firstRoot), Collection(secondRoot), "squeezr-0a1b2c3d4e5f" }
                .OrderBy(name => name, StringComparer.Ordinal).ToList(),
            "every unclaimed mint-shaped name is a candidate — the scratch-shaped half is stopped by the " +
            "ownership gate, never by the claim set pretending to know its provenance");

        applied.Entries.Select(entry => entry.Collection).ToList().ShouldBe(listed,
            "the apply path works the preview's own list: same names, same endpoint, same order");
        applied.Entries.Select(entry => (entry.Endpoint, entry.Collection)).ToList()
            .ShouldBe(previewed.Entries.Select(entry => (entry.Endpoint, entry.Collection)).ToList());
        server.Deleted.ShouldBe(listed, "the deletion set is structurally the dry-run output, nothing more");
        applied.Entries.ShouldAllBe(entry => entry.Action == WorkspaceCollectionSweep.ActionReaped);
    }

    [Fact]
    public void SweepGate_TheDeleteRequestSitsBehindTheOwnedFlag()
    {
        var source = File.ReadAllText(RepoFile("src/Squeezr.Cli/Commands/WorkspaceCollectionSweep.cs"));

        var gateAt = source.IndexOf("if (!target.Owned)", StringComparison.Ordinal);
        var deleteAt = source.IndexOf("TryDeleteCollectionAsync(http, target.Endpoint", StringComparison.Ordinal);

        gateAt.ShouldBeGreaterThan(0,
            "the sweep consults the ownership the declaring config carried, not a conjunction restated " +
            "somewhere a refactor can drop it silently");
        deleteAt.ShouldBeGreaterThan(gateAt,
            "the delete request is downstream of the gate, and a non-owned candidate leaves the loop before it");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ClaimedNames_RowSpelledThroughAnAncestorLink_ClaimsBothMintedNames()
    {
        var tree = Tracked(TestDirs.NewTempDir());
        var lane = Path.Combine(tree, "lane");
        Directory.CreateDirectory(lane);
        var above = Tracked(TestDirs.NewTempDir());
        var link = Path.Combine(above, "tree-link");
        Directory.CreateSymbolicLink(link, tree);
        var spelledRoot = Path.Combine(link, "lane");
        var row = new WorkspaceRegistryEntry(
            "lane", spelledRoot, WorkspaceStoreKeys.Create("lane", spelledRoot),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var claimed = WorkspaceCollectionSweep.ClaimedNames([row]);

        claimed.ShouldContain(Collection(spelledRoot), "the row's own text names what its serve path opens");
        claimed.ShouldContain(Collection(WorkspaceCanonicalPath.ResolveThroughLinks(spelledRoot)),
            "and a reader walking in through the linked ancestor opens the other spelling of the same store");
        claimed.Count.ShouldBe(claimed.Distinct().Count());
        WorkspaceCanonicalPath.Canonicalize(spelledRoot).ShouldBe(spelledRoot,
            "the final-component resolve adds nothing for an ancestor link; the walk is the arm that does");
    }

    [Theory]
    [InlineData("squeezr-0a1b2c3d4e5f", true)]
    [InlineData("squeezr-d23ab1c2", false)]
    [InlineData("squeezr-0a1b2c3d4e5ff", false)]
    [InlineData("squeezr-demo", false)]
    [InlineData("grimoire-0a1b2c3d4e5f", false)]
    [InlineData("overmind_entries", false)]
    [InlineData("overmind_routes", false)]
    [InlineData("squeezr-0A1B2C3D4E5F", false)]
    public void IsMintedName_AcceptsOnlyWhatThisProductWrites(string collection, bool expected) =>
        WorkspaceCollectionSweep.IsMintedName(collection).ShouldBe(expected);

    // The whole truth table of the destructive gate, in one pure predicate: --apply alone is not consent to
    // delete, and a row phase that refused, declined the prompt, died mid-sweep or merely previewed has not
    // earned a delete on any endpoint even holding --apply. ModeNone is the confirmed no-op sweep: nothing
    // was left to prune, and --apply on that run is consent, not an accident.
    [Theory]
    [InlineData(true, WorkspaceRepairPruner.ModePruned, true)]
    [InlineData(true, WorkspaceRepairPruner.ModeNone, true)]
    [InlineData(true, WorkspaceRepairPruner.ModeDryRun, false)]
    [InlineData(true, WorkspaceRepairPruner.ModeRefused, false)]
    [InlineData(true, WorkspaceRepairPruner.ModeDeclined, false)]
    [InlineData(true, WorkspaceRepairPruner.ModeAborted, false)]
    [InlineData(false, WorkspaceRepairPruner.ModePruned, false)]
    [InlineData(false, WorkspaceRepairPruner.ModeNone, false)]
    [InlineData(false, WorkspaceRepairPruner.ModeDryRun, false)]
    [InlineData(false, WorkspaceRepairPruner.ModeRefused, false)]
    [InlineData(false, WorkspaceRepairPruner.ModeDeclined, false)]
    [InlineData(false, WorkspaceRepairPruner.ModeAborted, false)]
    public void CollectionApplyGate_OnlyAppliedConfirmedSweepOpensTheDoor(bool apply, string pruneMode, bool expected) =>
        WorkspaceCollectionSweep.CollectionApplyGate(apply, pruneMode).ShouldBe(expected);

    [Fact]
    public void RepairCommand_CollectionDoorReadsThePinnedGate()
    {
        var source = File.ReadAllText(RepoFile("src/Squeezr.Cli/Commands/WorkspaceRepairCommand.cs"));

        source.ShouldContain("CollectionApplyGate(settings.Apply, prune.Mode)", Case.Sensitive,
            "the destructive door opens only through the pinned predicate, never through a conjunction " +
            "restated at the call site where a refactor can drop it silently");
    }

    [Fact]
    public void EndpointsOf_RowsOnDistinctEndpoints_ListsEachOnce_AndNamesNothingWithoutAReadableConfig()
    {
        var first = Tracked(TestDirs.NewTempDir());
        var second = Tracked(TestDirs.NewTempDir());
        var bare = Tracked(TestDirs.NewTempDir());
        WriteConfig(first, "http://one.test:6334");
        WriteConfig(second, "http://two.test:6335");
        var rows = new[] { RowOf("one", first), RowOf("two", second), RowOf("three", second), RowOf("bare", bare) };

        var endpoints = WorkspaceCollectionSweep.EndpointsOf(rows);

        endpoints.ShouldBe(["http://one.test:6334", "http://two.test:6335"],
            "two rows on one endpoint are one survey, and a row whose config is gone adds no endpoint");
        WorkspaceCollectionSweep.EndpointsOf([RowOf("bare", bare)]).ShouldBeEmpty(
            "a guessed shared port would send repair to a Qdrant no row of this profile ever wrote to");
    }

    [Fact]
    public void RepairCommand_SurveysCollections_AfterTheRowSweep()
    {
        var source = File.ReadAllText(RepoFile("src/Squeezr.Cli/Commands/WorkspaceRepairCommand.cs"));

        var pruneAt = source.IndexOf("WorkspaceRepairPruner.RunAsync(", StringComparison.Ordinal);
        var surveyAt = source.IndexOf("SurveyCollections(settings", StringComparison.Ordinal);

        pruneAt.ShouldBeGreaterThan(0);
        surveyAt.ShouldBeGreaterThan(pruneAt,
            "the claim set is what survived the row sweep, so the survey runs after it, never before");
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        throw new InvalidOperationException($"Repository root not found above {AppContext.BaseDirectory}.");
    }

    private static string Collection(string root) => QdrantVectorStore.GetCollectionName(root);

    private (string Root, string StoreKey, string StoreDir) WriteRow(string name)
    {
        var root = Tracked(TestDirs.NewTempDir());
        var storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "x");
        return (root, storeKey, storeDir);
    }

    private void RemoveRow(string name)
    {
        var row = WorkspaceRegistry.Load().Single(entry => entry.Name == name);
        WorkspaceRegistry.RemoveByStoreKey(row.StoreKey);
    }

    private static WorkspaceRegistryEntry RowOf(string name, string root) =>
        new(name, root, WorkspaceStoreKeys.Create(name, root), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static QdrantSweepTarget Owned(string endpoint) => new(endpoint, true);

    private static QdrantSweepTarget Foreign(string endpoint) => new(endpoint, false);

    private static void WriteConfig(string root, string endpoint, bool owned = false) =>
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Qdrant = new QdrantConfig { Endpoint = endpoint, Owned = owned }
        });

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }

    private sealed class FakeQdrant(
        HttpResponseMessage? deleteResponse,
        bool unreachable,
        IReadOnlyList<string> seeded) : HttpMessageHandler
    {
        public List<string> Deleted { get; } = [];

        public List<string> Requests { get; } = [];

        public List<IReadOnlyList<string>> KeysAtListTime { get; } = [];

        public static FakeQdrant With(params string[] collections) => new(null, false, collections);

        public FakeQdrant RefusingDeletesWith(HttpStatusCode status, string detail) =>
            new(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(
                        "{\"status\":{\"error\":\"" + detail + "\"}}", System.Text.Encoding.UTF8, "application/json"),
                },
                unreachable,
                seeded);

        public FakeQdrant Unreachable() => new(deleteResponse, true, seeded);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request));

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (unreachable)
            {
                throw new HttpRequestException("connection refused");
            }

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/collections")
            {
                KeysAtListTime.Add(RegistryKeys());
                var payload = string.Join(",", seeded.Select(name => $"{{\"collection_name\":\"{name}\"}}"));
                return Response($"{{\"result\":{{\"collections\":[{payload}]}}}}");
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/collections/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/collections/".Length..]);
                Deleted.Add(name);
                return deleteResponse ?? Response("{\"result\":true}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static IReadOnlyList<string> RegistryKeys()
        {
            var path = WorkspaceRegistry.FilePath;
            if (!File.Exists(path))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return [.. document.RootElement.EnumerateArray()
                .Select(row => row.GetProperty("storeKey").GetString())
                .Where(key => key is not null)
                .Select(key => key!)];
        }

        private static HttpResponseMessage Response(string content) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}
