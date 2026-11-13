using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// The refusal class a store hands back when it will not open. This door used to answer one bit, and every
// consumer that reports WHY a store is blank had to invent a cause it never measured (review-116-12 W-2).
// Each value here is pinned against the exception the open really raises on this box, not against a guess
// about what a broken file ought to throw.
[Collection("ProcessGlobals")]
public sealed class MetaStoreTryOpenCauseTests : IDisposable
{
    private readonly string _dir;

    public MetaStoreTryOpenCauseTests() => _dir = TestDirs.NewTempDir();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                File.SetUnixFileMode(Path.Combine(_dir, "denied.db"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void TryOpen_WithCause_AOpensAHealthyStoreAndNamesNoCause()
    {
        var path = Path.Combine(_dir, "healthy.db");
        using (var created = new MetaStore(path, createIfMissing: true))
        {
            created.AddRepo(new RepoDocument { Id = 1, Name = "demo", Path = "." });
        }

        var store = MetaStore.TryOpen(path, out var cause);

        store.ShouldNotBeNull();
        cause.ShouldBe(StoreOpenCause.None);
        store!.Dispose();
    }

    // The one 'missing' this door can state is the probe MetaStore itself runs: LiteDB's shared connection
    // creates lazily and would answer an absent path with a handle rather than a refusal.
    [Fact]
    public void TryOpen_WithCause_NamesMissingForAPathThatDoesNotExist()
    {
        var store = MetaStore.TryOpen(Path.Combine(_dir, "nope.db"), out var cause);

        store.ShouldBeNull();
        cause.ShouldBe(StoreOpenCause.Missing);
    }

    // A header whose fields do not parse is the corruption BUGS/030 describes: LiteDB answers 103, and a
    // fleet or stats blank that used to read as 'no store' now says the file is there and unreadable.
    [Fact]
    public void TryOpen_WithCause_NamesUnparseableForAHeaderThatDoesNotParse()
    {
        var path = Path.Combine(_dir, "broken.db");
        var bytes = new byte[8192];
        new Random(11).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        var store = MetaStore.TryOpen(path, out var cause);

        store.ShouldBeNull();
        cause.ShouldBe(StoreOpenCause.Unparseable);
        File.Exists(path).ShouldBeTrue("the refusal is about the bytes, not about the file being absent");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void TryOpen_WithCause_NamesDeniedForAFileThisProcessMayNotRead()
    {
        var path = Path.Combine(_dir, "denied.db");
        using (var created = new MetaStore(path, createIfMissing: true))
        {
            created.AddRepo(new RepoDocument { Id = 1, Name = "demo", Path = "." });
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        // the premise is asserted, not assumed: the open must actually be refused to this process, or the
        // class this test pins would be a reading of a door that stayed open
        File.Exists(path).ShouldBeTrue();
        Should.Throw<UnauthorizedAccessException>(() => File.ReadAllBytes(path));

        var store = MetaStore.TryOpen(path, out var cause);

        store.ShouldBeNull();
        cause.ShouldBe(StoreOpenCause.Denied);
    }

    // The one-bit door still answers, and answers the same way: this overload is additive, not a replacement.
    [Fact]
    public void TryOpen_WithoutCause_StillRefusesAStoreThatWillNotOpen()
    {
        var path = Path.Combine(_dir, "broken2.db");
        File.WriteAllBytes(path, new byte[8192]);

        MetaStore.TryOpen(path).ShouldBeNull();
    }

    // The report that used to say 'meta.db would not open' and point at a registry sweep now carries the
    // measured class and points at the door that works on this state.
    [Fact]
    public void UnreadableStore_CarriesTheCauseAsAField_AndDoesNotCallRepairTheDoor()
    {
        var outcome = WorktreeReconcileOutcome.UnreadableStore("/work/lane", StoreOpenCause.Unparseable);

        outcome.StoreFailure.ShouldBe(StoreOpenCause.Unparseable);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.StoreUnreadableCode);
        outcome.Reason.ShouldNotBeNull();
        outcome.Reason.Contains("Unparseable", StringComparison.Ordinal).ShouldBeTrue();
        outcome.Reason.Contains($"Run '{RemedyText.WorkspaceRepairCommand}'", StringComparison.Ordinal)
            .ShouldBeFalse(
                "workspace repair sweeps registry rows and never opens a meta.db (review-116-12 D8), so it is "
                + "not the first door for this state");
        outcome.Reason.Contains(RemedyText.DropCommand, StringComparison.Ordinal).ShouldBeTrue(
            "the remedy is the one that replaces the file");
    }
}
