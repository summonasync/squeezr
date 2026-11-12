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
            [Endpoint], apply: false, CancellationToken.None, server);

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
            [Endpoint], apply: true, CancellationToken.None, server);

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
            [Endpoint], apply: true, CancellationToken.None, server);

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
            [Endpoint], apply: true, CancellationToken.None, server);

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
            [Endpoint], apply: true, CancellationToken.None, server);

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
    public void IsMintedName_AcceptsOnlyWhatThisProductWrites(string collection, bool expected) =>
        WorkspaceCollectionSweep.IsMintedName(collection).ShouldBe(expected);

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

    private static void WriteConfig(string root, string endpoint) =>
        ConfigFile.Save(root, SqueezrConfig.CreateDefault() with
        {
            Qdrant = new QdrantConfig { Endpoint = endpoint }
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
