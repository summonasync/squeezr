using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Roslyn;

public class PacedSemanticPassTests
{
    [Fact]
    public void ColdPacedPass_ParsesWholeRepoAndWarmsSession()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        var progress = new PhaseProgress();

        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], progress);

        pool.CreatedSessions.ShouldBe(1);
        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeTrue();
        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
    }

    [Fact]
    public void WarmPacedPass_ParsesOnlyTouchedFile()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"pacedneedle\"; }");
        var progress = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Notes.cs"]);
        pool.CreatedSessions.ShouldBe(1);
    }

    [Fact]
    public void WarmPacedPass_KeepsUnchangedFileRefRowsInPlace()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());
        var before = RefRows(workspace, "src/Calc.cs").Select(r => r.Id).ToList();
        before.ShouldNotBeEmpty();

        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"keystoneedle\"; }");
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        RefRows(workspace, "src/Calc.cs").Select(r => r.Id).ToList().ShouldBe(before);
    }

    [Fact]
    public void WarmPacedPass_IndexesEditedContentThroughReusedSession()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;

                public int Subtract(int a, int b) => a - b;
            }
            """);
        Index(workspace, pool, [RepoFile(workspace, "src/Calc.cs")], new PhaseProgress());

        var names = Symbols(workspace, "src/Calc.cs").Select(s => s.Name).ToList();
        names.ShouldContain("Subtract");
        names.ShouldContain("Add");
        pool.CreatedSessions.ShouldBe(1);
    }

    [Fact]
    public void WarmPacedPass_IndexesFileCreatedAfterSessionOpened()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "src/Fresh.cs", """
            public static class Fresh
            {
                public static int Make() => Calc.Add(1, 2);
            }
            """);
        var progress = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Fresh.cs")], progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Fresh.cs"]);
        Symbols(workspace, "src/Fresh.cs").Select(s => s.Name).ShouldContain("Make");
        pool.CreatedSessions.ShouldBe(1);
    }

    [Fact]
    public void WarmPacedPass_DeletedFile_LeavesNoSymbolsOrRefs()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        File.Delete(RepoFile(workspace, "src/Use.cs"));
        Index(workspace, pool, [RepoFile(workspace, "src/Use.cs")], new PhaseProgress());

        Symbols(workspace, "src/Use.cs").ShouldBeEmpty();
        RefRows(workspace, "src/Calc.cs").ShouldBeEmpty();
        pool.CreatedSessions.ShouldBe(1);
    }

    [Fact]
    public void WarmPacedPass_RenamedFile_MovesSymbolsToNewPath()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        File.Move(RepoFile(workspace, "src/Use.cs"), RepoFile(workspace, "src/Used.cs"));
        WriteFile(workspace, "src/Used.cs", """
            public static class Use
            {
                public static int Run()
                {
                    return Calc.Add(3, 4);
                }
            }
            """);
        Index(
            workspace, pool,
            [RepoFile(workspace, "src/Use.cs"), RepoFile(workspace, "src/Used.cs")],
            new PhaseProgress());

        Symbols(workspace, "src/Use.cs").ShouldBeEmpty();
        Symbols(workspace, "src/Used.cs").Select(s => s.Name).ShouldContain("Run");
        RefRows(workspace, "src/Calc.cs").ShouldBeEmpty();
    }

    [Fact]
    public void WarmPacedPass_UsageAddedInCaller_HealsWhenDeclaringFileIsTouched()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());
        var before = RefRows(workspace, "src/Calc.cs").Count;
        before.ShouldBeGreaterThan(0);

        WriteFile(workspace, "src/Use.cs", """
            public static class Use
            {
                public static int Run()
                {
                    var first = Calc.Add(1, 2);
                    return first + Calc.Add(3, 4);
                }
            }
            """);
        Index(workspace, pool, [RepoFile(workspace, "src/Use.cs")], new PhaseProgress());

        RefRows(workspace, "src/Calc.cs").Count.ShouldBe(before);

        WriteFile(workspace, "src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;

                public int Twice(int a) => a * 2;
            }
            """);
        Index(workspace, pool, [RepoFile(workspace, "src/Calc.cs")], new PhaseProgress());

        RefRows(workspace, "src/Calc.cs").Count.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void ForcedPacedPass_DropsSessionAndParsesWholeRepo()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        var progress = new PhaseProgress();
        Index(workspace, pool, [], progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
        pool.CreatedSessions.ShouldBe(2);
    }

    [Fact]
    public void ExplicitIndex_WithoutPool_ParsesWholeRepoEveryPass()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"unpacedneedle\"; }");
        var progress = new PhaseProgress();
        new WorkspaceIndexer().Index(workspace, progress: progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
    }

    private static void Index(
        string workspace,
        SemanticSessionPool pool,
        IReadOnlyList<string> changedPaths,
        PhaseProgress progress) =>
        new WorkspaceIndexer().Index(
            workspace, progress: progress, changedPaths: changedPaths, sessions: pool);

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

    private static string RepoFile(string workspace, string relativePath) => Path.Combine(RepoRoot(workspace), relativePath);

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
                public static int Run() => Calc.Add(1, 2);
            }
            """);
        WriteFile(workspace, "src/Notes.cs", """
            public static class Notes
            {
                public static string Term = "flatneedle";
            }
            """);
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = RepoFile(workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static List<SymbolDocument> Symbols(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var file = store.ListFiles(repoDoc.Id).FirstOrDefault(f => f.Path == relativePath);
        return file is null ? [] : store.ListSymbolsByFile(file.Id).ToList();
    }

    private static List<RefDocument> RefRows(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var file = store.ListFiles(repoDoc.Id).FirstOrDefault(f => f.Path == relativePath);
        return file is null
            ? []
            : store.ListSymbolsByFile(file.Id).SelectMany(s => store.ListRefsByFromSymbol(s.Id)).ToList();
    }

    private sealed class PhaseProgress : IIndexProgress
    {
        private readonly object _gate = new();
        private readonly List<(IndexPhase Phase, string Item)> _items = [];
        private IndexPhase _current;

        public IReadOnlyList<string> Items(IndexPhase phase)
        {
            lock (_gate)
            {
                return _items.Where(i => i.Phase == phase).Select(i => i.Item).Order().ToList();
            }
        }

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
        {
            lock (_gate)
            {
                _current = phase;
            }
        }

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
        {
            lock (_gate)
            {
                _items.Add((_current, currentItem));
            }
        }

        public void RepoCompleted(string repoName)
        {
        }
    }
}
