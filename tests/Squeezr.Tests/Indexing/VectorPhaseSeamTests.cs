using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// The inversion the carve lives on: the pass only ever sees the seam, so a stub standing in for the
// semantic assembly pins where Core calls it and how the pass aggregates what comes back.
public class VectorPhaseSeamTests
{
    [Fact]
    public void Index_WithAStubPhase_DrainsEachRepoAndAggregatesTheCounts()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "second/b.cs", Source);
        var manager = new WorkspaceManager(workspace);
        manager.AddRepo("second", "second");
        var stub = new StubPhase(drainResult: (3, false));

        var result = new WorkspaceIndexer().Index(workspace, vectorPhase: stub);

        stub.Calls.ShouldContain("preflight");
        stub.Calls.ShouldContain("replay");
        stub.DrainRepos.ShouldBe(2);
        stub.Calls.ShouldNotContain("drop-collection");
        result.Embedded.ShouldBe(6);
        result.DrainCompleted.ShouldBeFalse();
        result.DrainSkippedReason.ShouldBeNull();
    }

    [Fact]
    public void Index_EmbedBudget_CarriesTheRemainderAcrossRepos()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "second/b.cs", Source);
        var manager = new WorkspaceManager(workspace);
        manager.AddRepo("second", "second");
        var stub = new StubPhase(drainResult: (3, true));

        new WorkspaceIndexer().Index(workspace, vectorPhase: stub, embedBudgetPerPass: 5);

        stub.Budgets.ShouldBe([5, 2]);
    }

    [Fact]
    public void Index_NullPhase_RunsExactOnly()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);

        var result = new WorkspaceIndexer().Index(workspace, semanticUnavailableReason: "provider unavailable");

        result.Embedded.ShouldBe(0);
        result.DrainCompleted.ShouldBeFalse();
        result.DrainSkippedReason.ShouldBe("provider unavailable");
    }

    [Fact]
    public void Index_Reembed_DropsTheCollectionBeforeThePasses()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var stub = new StubPhase(drainResult: (0, true));

        new WorkspaceIndexer().Index(workspace, vectorPhase: stub, reembed: true);

        stub.Calls.IndexOf("drop-collection").ShouldBeLessThan(stub.Calls.IndexOf("drain"));
    }

    private const string Source = "class Alpha { string Term = \"seambody\"; }";

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        Directory.CreateDirectory(Path.Combine(workspace, "second"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class StubPhase((int Embedded, bool Completed) drainResult) : IIndexVectorPhase
    {
        public readonly List<string> Calls = [];
        public readonly List<int?> Budgets = [];
        public int DrainRepos { get; private set; }

        public string ExecutorDevice => "stub";

        public string ProviderName => "stub";

        public void DropCollection(List<string> warnings) => Calls.Add("drop-collection");

        public void Preflight(List<string> warnings) => Calls.Add("preflight");

        public void ReplayJournal(MetaStore store, List<string> warnings) => Calls.Add("replay");

        public int RetirePoints(MetaStore store, IReadOnlyList<PendingReapTarget> files, List<string> warnings, SecretGuardIngestion guard)
        {
            Calls.Add("retire");
            return 0;
        }

        public (int Embedded, bool Completed) DrainRepo(
            RepoDocument repo, MetaStore store, SqueezrConfig config, List<string> warnings, IIndexProgress? progress, int? budget)
        {
            Calls.Add("drain");
            DrainRepos++;
            Budgets.Add(budget);
            return drainResult;
        }

        public void DrainFile(RepoDocument repo, MetaStore store, SqueezrConfig config, List<string> warnings) =>
            Calls.Add("drain-file");
    }
}
