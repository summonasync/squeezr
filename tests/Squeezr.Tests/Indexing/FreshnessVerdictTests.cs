using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing;

public class FreshnessVerdictTests
{
    [Fact]
    public void Classify_KnownStates_FreshLaggingUnknown()
    {
        var root = NewStoreRoot(out var store);
        var repo = InsertRepo(store, "alpha");
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var freshPath = Path.Combine(root, "repo", "Fresh.cs");
        File.WriteAllText(freshPath, "class Fresh { }");
        InsertFile(store, repo.Id, "Fresh.cs", sizeBytes: File.ReadAllText(freshPath).Length,
            indexedAt: DateTime.UtcNow.AddMinutes(1));

        var stalePath = Path.Combine(root, "repo", "Stale.cs");
        File.WriteAllText(stalePath, "class Stale { }");
        InsertFile(store, repo.Id, "Stale.cs", sizeBytes: new FileInfo(stalePath).Length,
            indexedAt: DateTime.UtcNow.AddHours(-2));

        var verdicts = FreshnessVerdict.Classify(
            root,
            [("alpha", "Fresh.cs"), ("alpha", "Stale.cs"), ("alpha", "Ghost.cs"), ("beta", "Foreign.cs")],
            store);

        var fresh = verdicts.Single(v => v.Path == "Fresh.cs");
        fresh.Verdict.ShouldBe(FreshnessVerdict.Fresh);
        fresh.LagSeconds.ShouldBeNull();

        var lagging = verdicts.Single(v => v.Path == "Stale.cs");
        lagging.Verdict.ShouldBe(FreshnessVerdict.Lagging);
        lagging.LagSeconds.ShouldNotBeNull();
        lagging.LagSeconds!.Value.ShouldBeGreaterThan(3600);

        verdicts.Single(v => v.Path == "Ghost.cs").Verdict.ShouldBe(FreshnessVerdict.Unknown);
        verdicts.Single(v => v.Path == "Foreign.cs").Verdict.ShouldBe(FreshnessVerdict.Unknown);
    }

    [Fact]
    public void Classify_SizeMismatch_LagsWithoutClockSkew()
    {
        var root = NewStoreRoot(out var store);
        var repo = InsertRepo(store, "alpha");
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var path = Path.Combine(root, "repo", "Grown.cs");
        File.WriteAllText(path, "class Grown { }");
        var indexedAt = DateTime.UtcNow.AddMinutes(-1);
        File.AppendAllText(path, "int extra;");
        InsertFile(store, repo.Id, "Grown.cs", sizeBytes: 10, indexedAt: indexedAt);

        var verdict = FreshnessVerdict.Classify(root, [("alpha", "Grown.cs")], store).Single();

        verdict.Verdict.ShouldBe(FreshnessVerdict.Lagging);
    }

    [Fact]
    public void Classify_SizeMismatch_WithOlderMtime_LagsWithoutLagSeconds()
    {
        var root = NewStoreRoot(out var store);
        var repo = InsertRepo(store, "alpha");
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var path = Path.Combine(root, "repo", "Rewritten.cs");
        File.WriteAllText(path, "class Rewritten { }");
        InsertFile(store, repo.Id, "Rewritten.cs", sizeBytes: 500, indexedAt: DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(path, "class Rewritten { void NowMuchLonger() { } }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));

        var verdict = FreshnessVerdict.Classify(root, [("alpha", "Rewritten.cs")], store).Single();

        verdict.Verdict.ShouldBe(FreshnessVerdict.Lagging);
        verdict.LagSeconds.ShouldBeNull();
    }

    [Fact]
    public void Classify_GuardStaleKey_OverridesStatFresh()
    {
        var root = NewStoreRoot(out var store);
        var repo = InsertRepo(store, "alpha");
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var path = Path.Combine(root, "repo", "Blocked.cs");
        File.WriteAllText(path, "class Blocked { }");
        InsertFile(store, repo.Id, "Blocked.cs", sizeBytes: new FileInfo(path).Length,
            indexedAt: DateTime.UtcNow.AddMinutes(1));

        var verdict = FreshnessVerdict.Classify(
            root, [("alpha", "Blocked.cs")], store,
            guardStaleKeys: ["alpha/Blocked.cs"]).Single();

        verdict.Verdict.ShouldBe(FreshnessVerdict.Lagging);
    }

    [Fact]
    public void Classify_MissingStore_AllUnknownWithoutThrowing()
    {
        var root = TestDirs.NewTempDir();

        var verdicts = FreshnessVerdict.Classify(root, [("alpha", "a.cs")]);

        verdicts.Single().Verdict.ShouldBe(FreshnessVerdict.Unknown);
    }

    private static string NewStoreRoot(out MetaStore store)
    {
        var root = TestDirs.NewTempDir();
        var storeDir = Path.Combine(root, ".squeezr");
        Directory.CreateDirectory(storeDir);
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        var manager = new Squeezr.Core.Workspace.WorkspaceManager(root);
        manager.Init("demo");
        manager.AddRepo("repo", "alpha");
        store = new MetaStore(Path.Combine(storeDir, "meta.db"), createIfMissing: true);
        return root;
    }

    private static RepoDocument InsertRepo(MetaStore store, string name)
    {
        var repo = new RepoDocument { Id = store.GetNextRepoId(), Name = name };
        store.UpsertRepo(repo);
        return repo;
    }

    private static void InsertFile(MetaStore store, uint repoId, string path, long sizeBytes, DateTime indexedAt)
    {
        store.UpsertFile(new FileDocument
        {
            Id = store.GetNextFileId(),
            RepoId = repoId,
            Path = path,
            SizeBytes = sizeBytes,
            IndexedAt = indexedAt,
        });
    }
}
