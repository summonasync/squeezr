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
    public void ForcedPass_WithWarmSession_KeepsSessionAndReparsesDriftOnly()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "src/Use.cs", """
            public static class Use
            {
                public static int Run() => Calc.Add(1, 2);

                public static int Again() => Calc.Add(3, 4);
            }
            """);
        var progress = new PhaseProgress();
        Index(workspace, pool, [], progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Use.cs"],
            "a forced pass with a warm session reconciles on sha drift instead of buying a whole-repo pass");
        pool.CreatedSessions.ShouldBe(1);
        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeTrue();
    }

    [Fact]
    public void ForcedPass_WithColdPool_ParsesWholeRepo()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        var progress = new PhaseProgress();

        Index(workspace, pool, [], progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
        pool.CreatedSessions.ShouldBe(1);
    }

    [Fact]
    public void ForcedPass_WithFullFlag_DropsSessionAndParsesWholeRepo()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        var progress = new PhaseProgress();
        new WorkspaceIndexer().Index(workspace, full: true, progress: progress, changedPaths: [], sessions: pool);

        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
        pool.CreatedSessions.ShouldBe(2);
    }

    [Fact]
    public void PacedPass_WhenSessionDegrades_IsNeverTreatedAsIncremental()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        WriteFile(workspace, "broken.csproj", "<this is not a project file");
        var clock = new FakeClock();
        using var pool = new SemanticSessionPool(() => clock.Now);
        var cold = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], cold);

        cold.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeFalse(
            "a fallback session must not answer warm for the mode it was asked to open");

        clock.Advance(TimeSpan.FromMinutes(1));
        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"degradedneedle\"; }");
        var second = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], second);

        second.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"],
            "the degraded pass cannot scope like an incremental one");
        pool.CreatedSessions.ShouldBe(2, "the retry is paid again once the window passes, not cached");
        Symbols(workspace, "src/Notes.cs").Select(s => s.Name).ShouldContain("Term");
    }

    [Fact]
    public void ThrottledPass_KeepsEveryCsharpFileInScopeForTheRetry()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        WriteFile(workspace, "broken.csproj", "<this is not a project file");
        var clock = new FakeClock();
        using var pool = new SemanticSessionPool(() => clock.Now);
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        clock.Advance(TimeSpan.FromSeconds(10));
        WriteFile(
            workspace,
            "src/Notes.cs",
            """
            public static class Notes
            {
                public static string Term = "throttleneedle";

                public static string Retry = "afterwindow";
            }
            """);
        var throttled = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], throttled);

        throttled.Items(IndexPhase.Semantic).ShouldBeEmpty(
            "inside the retry window the pass buys no session at all instead of a second solution open");
        pool.DeferredSemantic(RepoRoot(workspace)).Order().ToList().ShouldBe(
            ["src/Calc.cs", "src/Notes.cs", "src/Use.cs"],
            "the walk committed these shas, so the skipped files have to stay in scope for the retry");

        clock.Advance(TimeSpan.FromMinutes(1));
        var retried = new PhaseProgress();
        Index(workspace, pool, [], retried);

        retried.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs", "src/Use.cs"]);
        pool.DeferredSemantic(RepoRoot(workspace)).ShouldBeEmpty();
        pool.CreatedSessions.ShouldBe(2);
        Symbols(workspace, "src/Notes.cs").Select(s => s.Name).ShouldContain("Retry",
            "the retry closes the hole the throttled pass left instead of freezing it");
    }

    [Fact]
    public void WarmPacedPass_SkippedSemanticFile_IsReparsedOnTheNextPass()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "src/Notes.cs", "??? not csharp at all\n");
        var refused = new PhaseProgress();
        var refusedResult = new WorkspaceIndexer().Index(
            workspace, progress: refused, changedPaths: [RepoFile(workspace, "src/Notes.cs")], sessions: pool);

        refused.Items(IndexPhase.Semantic).ShouldBe(["src/Notes.cs"]);
        refusedResult.Warnings.ShouldContain(w => w.Contains("symbol extraction failed"),
            "the refusal must be visible, not just a silent skip");
        Symbols(workspace, "src/Notes.cs").Select(s => s.Name).ShouldContain("Term",
            "a file the semantic phase refused keeps the rows it had");
        pool.DeferredSemantic(RepoRoot(workspace)).ShouldContain("src/Notes.cs",
            "the refusal has to be booked into the per-repo deferred set");

        // the walk committed the sha of the refused bytes already, so from here a pass scoped to another
        // file can only bring Notes.cs back through the deferred set — that is what stops the sha freeze
        var next = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Calc.cs")], next);

        next.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs"],
            "a pass scoped elsewhere must still pull the deferred file into the parse area");

        WriteFile(workspace, "src/Notes.cs", """
            public static class Notes
            {
                public static string Term = "flatneedle";

                public static string Retry = "deferredneedle";
            }
            """);
        var healed = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "src/Calc.cs")], healed);

        healed.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs", "src/Notes.cs"],
            "the committed sha must not freeze a file the semantic phase skipped");
        Symbols(workspace, "src/Notes.cs").Select(s => s.Name).ShouldContain("Retry");
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

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("src/Extras.slnx")]
    public void WarmPacedPass_WithProjectLayoutFileInBatch_ReopensSessionAndKeepsTheParseAreaScoped(string relativePath)
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());
        pool.CreatedSessions.ShouldBe(1);
        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeTrue();

        WriteFile(workspace, relativePath, "<Project><PropertyGroup><Version>2</Version></PropertyGroup></Project>");
        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"reopenneedle\"; }");
        var progress = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, relativePath), RepoFile(workspace, "src/Notes.cs")], progress);

        pool.CreatedSessions.ShouldBe(2, $"'{relativePath}' in the batch makes the opened project graph wrong, so the session reopens from disk");
        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeTrue();
        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Notes.cs"],
            "the reopen is one solution open, not a whole-repo semantic pass");
        Symbols(workspace, "src/Notes.cs").Select(s => s.Name).ShouldContain("Term");
    }

    [Fact]
    public void WarmPacedPass_WithLayoutFileOnlyInBatch_DropsTheSessionAndParsesNothing()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "Directory.Packages.props", "<Project />");
        var progress = new PhaseProgress();
        Index(workspace, pool, [RepoFile(workspace, "Directory.Packages.props")], progress);

        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeFalse(
            "a restructured solution cannot be healed by a scoped parse, so the stale session must not survive");
        pool.CreatedSessions.ShouldBe(1, "the drop itself buys no second solution open while the batch holds no csharp work");
        progress.Items(IndexPhase.Semantic).ShouldBeEmpty();
    }

    [Fact]
    public void WarmPacedPass_WithLayoutLookalikesInBatch_KeepsTheSession()
    {
        var workspace = NewWorkspace();
        WriteRepo(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, [RepoFile(workspace, "src/Notes.cs")], new PhaseProgress());

        WriteFile(workspace, "Squeezr.slnx.bak", "<Project />");
        WriteFile(workspace, "MyApp.csproj.user", "<Project />");
        WriteFile(workspace, "notes.unknownext", "noise");
        WriteFile(workspace, "src/Notes.cs", "public static class Notes { public static string Term = \"keptneedle\"; }");
        var progress = new PhaseProgress();
        Index(
            workspace, pool,
            [
                RepoFile(workspace, "Squeezr.slnx.bak"),
                RepoFile(workspace, "MyApp.csproj.user"),
                RepoFile(workspace, "notes.unknownext"),
                RepoFile(workspace, "src/Notes.cs")
            ],
            progress);

        pool.CreatedSessions.ShouldBe(1, "an editor backup or a temp file is not a layout change");
        pool.IsWarm(RepoRoot(workspace), semantic: true).ShouldBeTrue();
        progress.Items(IndexPhase.Semantic).ShouldBe(["src/Notes.cs"]);
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

    private sealed class FakeClock(DateTime? start = null)
    {
        public DateTime Now { get; private set; } = start ?? new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        public void Advance(TimeSpan by) => Now += by;
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
