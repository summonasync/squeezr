using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Mcp;

public class CostBaselineTests
{
    [Fact]
    public void Measure_KnownFile_BaselineExceedsTokensWithConsistentMath()
    {
        using var store = NewStore();
        var repo = InsertRepo(store, "alpha");
        InsertFile(store, repo.Id, "src/Big.cs", sizeBytes: 4000);

        var (cost, unknown) = CostBaseline.Measure(
            store, [("alpha", "src/Big.cs")], tokensUsed: 200, complete: true);

        unknown.ShouldBe(0);
        cost.TokensUsed.ShouldBe(200);
        cost.BaselineTokens.ShouldNotBeNull();
        cost.BaselineTokens!.Value.ShouldBeGreaterThanOrEqualTo(4000 / 4);
        cost.SavedTokens.ShouldBe(cost.BaselineTokens!.Value - 200);
        cost.Ratio.ShouldBe(cost.BaselineTokens!.Value / 200.0);
        cost.Complete.ShouldBeTrue();
    }

    [Fact]
    public void Measure_MixedKnownAndUnknown_CountsUnknownExcludesFromBaseline()
    {
        using var store = NewStore();
        var repo = InsertRepo(store, "alpha");
        InsertFile(store, repo.Id, "src/Known.cs", sizeBytes: 800);

        var (cost, unknown) = CostBaseline.Measure(
            store, [("alpha", "src/Known.cs"), ("alpha", "src/Ghost.cs")], tokensUsed: 50, complete: false);

        unknown.ShouldBe(1);
        cost.BaselineTokens.ShouldNotBeNull();
        cost.SavedTokens.ShouldBe(cost.BaselineTokens!.Value - 50);
        cost.Complete.ShouldBeFalse();
    }

    [Fact]
    public void Measure_AllUnknown_CostIsUnknownNeverZero()
    {
        using var store = NewStore();
        InsertRepo(store, "alpha");

        var (cost, unknown) = CostBaseline.Measure(
            store, [("alpha", "src/Ghost.cs"), ("beta", "src/AlsoGhost.cs")], tokensUsed: 30, complete: true);

        unknown.ShouldBe(2);
        cost.BaselineTokens.ShouldBeNull();
        cost.SavedTokens.ShouldBeNull();
        cost.Ratio.ShouldBeNull();
        cost.TokensUsed.ShouldBe(30);
    }

    [Fact]
    public void Measure_DuplicateFiles_CountedOnce()
    {
        using var store = NewStore();
        var repo = InsertRepo(store, "alpha");
        InsertFile(store, repo.Id, "src/Twin.cs", sizeBytes: 400);

        var single = CostBaseline.Measure(
            store, [("alpha", "src/Twin.cs")], tokensUsed: 10, complete: true);
        var duplicated = CostBaseline.Measure(
            store, [("alpha", "src/Twin.cs"), ("alpha", "src/Twin.cs")], tokensUsed: 10, complete: true);

        duplicated.Cost.BaselineTokens.ShouldBe(single.Cost.BaselineTokens);
        duplicated.Cost.SavedTokens.ShouldBe(single.Cost.SavedTokens);
    }

    [Fact]
    public void Measure_MirroredRepos_SameRelativePath_CountedPerRepo()
    {
        using var store = NewStore();
        var alpha = InsertRepo(store, "alpha");
        var beta = InsertRepo(store, "beta");
        InsertFile(store, alpha.Id, "src/Guard.cs", sizeBytes: 400);
        InsertFile(store, beta.Id, "src/Guard.cs", sizeBytes: 800);

        var (cost, unknown) = CostBaseline.Measure(
            store, [("alpha", "src/Guard.cs"), ("beta", "src/Guard.cs")], tokensUsed: 10, complete: true);

        unknown.ShouldBe(0);
        cost.BaselineTokens.ShouldBe(TokenEstimate.WholeFile(400) + TokenEstimate.WholeFile(800));
    }

    [Fact]
    public void Measure_MissingStore_DegradesToUnknownWithoutThrowing()
    {
        var root = TestDirs.NewTempDir();

        var (cost, unknown) = CostBaseline.Measure(
            root, [("alpha", "src/Big.cs")], tokensUsed: 20, complete: true);

        unknown.ShouldBe(0);
        cost.BaselineTokens.ShouldBeNull();
        cost.SavedTokens.ShouldBeNull();
        cost.Ratio.ShouldBeNull();
        cost.Complete.ShouldBeTrue();
    }

    private static MetaStore NewStore()
    {
        var dir = TestDirs.NewTempDir();
        return new MetaStore(Path.Combine(dir, "meta.db"), createIfMissing: true);
    }

    private static RepoDocument InsertRepo(MetaStore store, string name)
    {
        var repo = new RepoDocument { Id = store.GetNextRepoId(), Name = name };
        store.UpsertRepo(repo);
        return repo;
    }

    private static void InsertFile(MetaStore store, uint repoId, string path, long sizeBytes)
    {
        store.UpsertFile(new FileDocument
        {
            Id = store.GetNextFileId(),
            RepoId = repoId,
            Path = path,
            SizeBytes = sizeBytes,
        });
    }
}
