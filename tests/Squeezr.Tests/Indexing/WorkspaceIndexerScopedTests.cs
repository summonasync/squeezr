using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// The scoped entry (025/T1 fix round): what a batch of announced paths buys, and what it refuses to buy.
// The fixtures are deliberately tiny — these cases pin the SHAPE of the pass (which rows move, which do not),
// while HookPostEditCostTests pins its COST. review-025-t1-01 finding 5 is the reason both exist: nothing
// measurable at this size tells a scoped pass from a repo-wide one.
public sealed class WorkspaceIndexerScopedTests
{
    [Fact]
    public void ScopedPass_HealsTheDeclarationTheCallerStoppedCalling_InTheSameRun()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);
        var useId = FileId(workspace, "src/Use.cs");
        DeclarationRefs(workspace, "src/Calc.cs").ShouldContain(r => r.FileId == useId);
        var declaredBefore = StoredRow(workspace, "src/Calc.cs")!.IndexedAt;

        WriteFile(workspace, "src/Use.cs", """
            public static class Use
            {
                public static int Run() => 42;
            }
            """);
        var result = new WorkspaceIndexer().IndexScoped(workspace, [RepoFile(workspace, "src/Use.cs")]);

        var ghosts = DeclarationRefs(workspace, "src/Calc.cs").Where(r => r.FileId == useId).ToList();
        ghosts.ShouldBeEmpty(
            "the fold of booked declarations is the only thing that retires this row inside the same run — " +
            "left booked, find_references keeps answering with a callsite the file no longer has");
        result.Indexed.ShouldBe(1);
        result.Removed.ShouldBe(0);
        (StoredRow(workspace, "src/Calc.cs")!.IndexedAt > declaredBefore).ShouldBeTrue(
            "the fold restamped the declaring row — the file was parsed, not just cleared");
        Symbols(workspace, "src/Calc.cs").Select(s => s.Name).ShouldContain("Add");
    }

    [Fact]
    public void ScopedPass_LeavesALegacyTrigramIndexAlone_AndStillWritesTheRows()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);
        var trigram = SqueezrPaths.TrigramIndexPath(workspace);
        var legacy = LegacyHeaderBytes();
        File.WriteAllBytes(trigram, legacy);

        WriteFile(workspace, "src/Notes.cs", """
            public static class Notes
            {
                public static string Term = "legacyneedle";
            }
            """);
        var result = new WorkspaceIndexer().IndexScoped(workspace, [RepoFile(workspace, "src/Notes.cs")]);

        File.ReadAllBytes(trigram).ShouldBe(legacy, "a batch that knows one path must not rewrite a repo-wide index");
        result.Warnings.ShouldContain(w => w.Contains("left the trigram index untouched"));
        result.Warnings.ShouldContain(w => w.Contains("version 1"));
        StoredRow(workspace, "src/Notes.cs")!.Sha256.ShouldBe(
            Sha256(File.ReadAllText(RepoFile(workspace, "src/Notes.cs"))),
            "the refusal is the trigram half only: the store side of the pass still lands");
    }

    [Fact]
    public void ScopedPass_AppendsItsOwnSegments_WhenTheIndexIsCurrent()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);
        var trigram = SqueezrPaths.TrigramIndexPath(workspace);
        var before = new FileInfo(trigram).Length;

        WriteFile(workspace, "src/Notes.cs", """
            public static class Notes
            {
                public static string Term = "appendedneedle";
            }
            """);
        var result = new WorkspaceIndexer().IndexScoped(workspace, [RepoFile(workspace, "src/Notes.cs")]);

        result.Warnings.ShouldNotContain(w => w.Contains("trigram index"));
        new FileInfo(trigram).Length.ShouldBeGreaterThan(before);
        TrigramSegment.TryReadVersion(trigram).ShouldBe(TrigramIndexFormat.CurrentVersion);
    }

    [Fact]
    public void ScopedPass_LeavesUnannouncedDriftExactlyWhereItWas()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);
        var quietBefore = StoredRow(workspace, "src/Use.cs")!.Sha256;

        WriteFile(workspace, "src/Use.cs", """
            public static class Use
            {
                public static int Run() => Hub(1);

                private static int Hub(int value) => value;
            }
            """);
        WriteFile(workspace, "src/Notes.cs", """
            public static class Notes
            {
                public static string Term = "scopedneedle";
            }
            """);
        var result = new WorkspaceIndexer().IndexScoped(workspace, [RepoFile(workspace, "src/Notes.cs")]);

        StoredRow(workspace, "src/Notes.cs")!.Sha256.ShouldBe(
            Sha256(File.ReadAllText(RepoFile(workspace, "src/Notes.cs"))));
        StoredRow(workspace, "src/Use.cs")!.Sha256.ShouldBe(quietBefore,
            "no walk, no reconciliation of paths nobody announced — this is the half the cost claim rests on");
        result.Indexed.ShouldBe(1);
    }

    [Fact]
    public void ScopedPass_ReapsAnAnnouncedPathThatIsGone()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);

        File.Delete(RepoFile(workspace, "src/Use.cs"));
        var result = new WorkspaceIndexer().IndexScoped(workspace, [RepoFile(workspace, "src/Use.cs")]);

        result.Removed.ShouldBe(1);
        StoredRow(workspace, "src/Use.cs").ShouldBeNull();
        Symbols(workspace, "src/Use.cs").ShouldBeEmpty();
        // the postings of the retired row leave with a tombstone, and the index itself stays current-format
        TrigramSegment.TryReadVersion(SqueezrPaths.TrigramIndexPath(workspace))
            .ShouldBe(TrigramIndexFormat.CurrentVersion);
    }

    [Fact]
    public void ScopedPass_DropsAStoredRowWhosePathTookADirectory_AndStillIndexesTheAnnouncedFile()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);
        var notes = RepoFile(workspace, "src/Notes.cs");
        var swapped = RepoFile(workspace, "src/Use.cs");
        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"keptneedle\"; }\n");

        // a directory that took the path of an indexed file is the shape no read can serve: the row goes,
        // the rest of the batch does not care
        File.Delete(swapped);
        Directory.CreateDirectory(swapped);

        var result = new WorkspaceIndexer().IndexScoped(workspace, [notes, swapped]);

        StoredRow(workspace, "src/Notes.cs")!.Sha256.ShouldBe(Sha256(File.ReadAllText(notes)));
        result.Indexed.ShouldBe(1);
        result.Removed.ShouldBe(1);
        StoredRow(workspace, "src/Use.cs").ShouldBeNull();
    }

    [Fact]
    public void ScopedPass_ResolvesEveryAnnouncedPathToItsOwnRepo_AndIgnoresTheOnesThatHaveNone()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "a"));
        Directory.CreateDirectory(Path.Combine(workspace, "b"));
        Directory.CreateDirectory(Path.Combine(workspace, "loose"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("a", "alpha");
        manager.AddRepo("b", "beta");
        WriteAt(workspace, "a/First.cs", "public static class First { public static int One = 1; }\n");
        WriteAt(workspace, "b/Second.cs", "public static class Second { public static int Two = 2; }\n");
        new WorkspaceIndexer().Index(workspace);

        var stray = WriteAt(workspace, "loose/Stray.cs", "public static class Stray { public static int Three = 3; }\n");
        var first = WriteAt(workspace, "a/First.cs", "public static class First { public static int One = 11; }\n");
        var second = WriteAt(workspace, "b/Second.cs", "public static class Second { public static int Two = 22; }\n");
        var result = new WorkspaceIndexer().IndexScoped(workspace, [first, second, stray]);

        result.Indexed.ShouldBe(2);
        StoredRow(workspace, "alpha", "First.cs")!.Sha256.ShouldBe(Sha256(File.ReadAllText(first)));
        StoredRow(workspace, "beta", "Second.cs")!.Sha256.ShouldBe(Sha256(File.ReadAllText(second)));
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.ListAllFiles().ShouldNotContain(f => f.Path.Contains("Stray"));
        }
    }

    [Fact]
    public void ScopedPass_KeepsAGuardedPathOutOfTheStore_AndNamesItNowhere()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);

        var secret = RepoFile(workspace, ".env");
        File.WriteAllText(secret, "API_KEY=ScopedGuardCanary\n");
        var result = new WorkspaceIndexer().IndexScoped(workspace, [secret]);

        result.Indexed.ShouldBe(0);
        result.Warnings.ShouldNotContain(w => w.Contains(".env"));
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.ListAllFiles().ShouldNotContain(f => f.Path.Contains(".env"));
        }
    }

    private static byte[] LegacyHeaderBytes()
    {
        var header = new byte[TrigramIndexFormat.HeaderSize];
        WriteU32(header, 0, TrigramIndexFormat.Magic);
        WriteU32(header, 4, 1u);
        return header;
    }

    private static void WriteU32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static string RepoRoot(string workspace) => Path.GetFullPath(Path.Combine(workspace, "repo"));

    private static string RepoFile(string workspace, string relativePath) =>
        Path.Combine(RepoRoot(workspace), relativePath);

    private static string WriteFile(string workspace, string relativePath, string content) =>
        WriteAt(workspace, Path.Combine("repo", relativePath), content);

    private static string WriteAt(string workspace, string relativePath, string content)
    {
        var full = Path.Combine(workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static void WriteRepo(string workspace)
    {
        WriteFile(workspace, "src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        WriteFile(workspace, "src/Use.cs", """
            public static class Use
            {
                public static int Run() => new Calc().Add(1, 2);
            }
            """);
        WriteFile(workspace, "src/Notes.cs", """
            public static class Notes
            {
                public static string Term = "flatneedle";
            }
            """);
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static FileDocument? StoredRow(string workspace, string relativePath) =>
        StoredRow(workspace, "demo", relativePath);

    private static FileDocument? StoredRow(string workspace, string repoName, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo(repoName)!;
        return store.ListFiles(repoDoc.Id).FirstOrDefault(f => f.Path == relativePath);
    }

    private static uint FileId(string workspace, string relativePath) =>
        StoredRow(workspace, relativePath)!.Id;

    private static List<SymbolDocument> Symbols(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var file = StoredRow(workspace, relativePath);
        return file is null ? [] : store.ListSymbolsByFile(file.Id).ToList();
    }

    // Rows whose declaring symbol lives in the given file, whatever file hosts the callsite. A row here whose
    // FileId belongs to a file that no longer contains the call is the ghost the fold exists to retire.
    private static List<RefDocument> DeclarationRefs(string workspace, string declaringRelativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var file = StoredRow(workspace, declaringRelativePath);
        return file is null
            ? []
            : store.ListSymbolsByFile(file.Id).SelectMany(s => store.ListRefsByFromSymbol(s.Id)).ToList();
    }
}
