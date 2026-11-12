using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class MetaStoreIndexStateTests
{
    [Fact]
    public void GetLatestFullIndexAt_NoStates_ReturnsNull()
    {
        using var store = NewStore();

        store.GetLatestFullIndexAt().ShouldBeNull();
    }

    [Fact]
    public void GetLatestFullIndexAt_ReturnsMaxAcrossStates()
    {
        using var store = NewStore();
        var older = DateTime.UtcNow.AddDays(-2);
        var newer = DateTime.UtcNow.AddDays(-1);
        store.SaveIndexState(new IndexStateDocument { RepoKey = "first", LastFullIndexAt = newer });
        store.SaveIndexState(new IndexStateDocument { RepoKey = "second", LastFullIndexAt = older });

        var latest = store.GetLatestFullIndexAt();

        latest.ShouldNotBeNull();
        latest!.Value.ToUniversalTime().ShouldBe(newer, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TryOpen_ValidFile_ReturnsStore()
    {
        var path = NewDbPath();
        using (var created = new MetaStore(path, createIfMissing: true))
        {
        }

        var opened = MetaStore.TryOpen(path);

        opened.ShouldNotBeNull();
        opened.Dispose();
    }

    [Fact]
    public void TryOpen_MissingFile_ReturnsNull()
    {
        MetaStore.TryOpen(NewDbPath()).ShouldBeNull();
    }

    // The header form beside the missing-file one: this door answers null, it does not hand a crash out.
    [Fact]
    public void TryOpen_HeaderFieldsAreGarbageBehindTheMagic_ReturnsNull()
    {
        var path = NewDbPath();
        using (var created = new MetaStore(path, createIfMissing: true))
        {
        }

        LiteDbHeaderCorruption.AfterTheMagic(path);

        MetaStore.TryOpen(path).ShouldBeNull();
        // The probe swallows this form; the loud door must keep throwing it, or a bad-argument bug inside
        // the open path would go silent for every writer.
        Should.Throw<ArgumentOutOfRangeException>(() => new MetaStore(path));
    }

    private static string NewDbPath() => Path.Combine(TestDirs.NewTempDir(), "meta.db");

    private static MetaStore NewStore() => new(NewDbPath(), createIfMissing: true);
}
