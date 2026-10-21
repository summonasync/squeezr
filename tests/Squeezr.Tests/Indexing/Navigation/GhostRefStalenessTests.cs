using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Indexing.Navigation;

public class GhostRefStalenessTests
{
    [Fact]
    public void WarmPass_WithRestampedCaller_LeavesTheGhostRowMarksTheDeclaringFileAndBooksIt()
    {
        var workspace = NewWorkspace();
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, ["src/Runner.cs"]);

        // the call is gone from the caller and only the caller is in this batch: the row naming it
        // lives under Calc.cs, and nothing in this pass can reach it
        WriteRepoFile(workspace, "src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return 1;
                }
            }
            """);
        Index(workspace, pool, ["src/Runner.cs"]);

        var result = new RefLookup(workspace).Find("Add", null);

        Rows(result).ShouldContain("src/Runner.cs:7",
            "the ghost row is still served — what the pass owes is the marker, not a silent edit");
        var declaration = result.LaggingDeclarations.ShouldHaveSingleItem();
        declaration.Repo.ShouldBe("demo");
        declaration.File.ShouldBe("src/Calc.cs");
        declaration.LaggingRows.ShouldBe(1);
        pool.DeferredSemantic(RepoRoot(workspace)).ShouldContain("src/Calc.cs",
            "the declaring file is booked into the next paced pass instead of a full reindex");
    }

    [Fact]
    public void BookedDeclaration_ReparsedInABoundedArea_AndTheLagMarkerClears()
    {
        var workspace = NewWorkspace();
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, ["src/Runner.cs"]);

        WriteRepoFile(workspace, "src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return 1;
                }
            }
            """);
        var ghostring = new PhaseProgress();
        Index(workspace, pool, ["src/Runner.cs"], ghostring);
        RowsOf(new RefLookup(workspace).Find("Add", null)).ShouldContain("src/Runner.cs:7");

        var healing = new PhaseProgress();
        Index(workspace, pool, [], healing);

        healing.Items(IndexPhase.Semantic).ShouldBe(["src/Calc.cs"],
            "the heal is the booked declaring file alone, not the repo");
        var result = new RefLookup(workspace).Find("Add", null);
        Rows(result).ShouldNotContain("src/Runner.cs:7", "the ghost is gone once the declaring side is re-parsed");
        Rows(result).ShouldContain("src/Other.cs:7", "the surviving caller keeps its row");
        result.LaggingDeclarations.ShouldBeNull("a rebuilt row set must not keep claiming lag");
    }

    [Fact]
    public void AddedCall_OnAKnownEdge_IsMissingUntilTheBookingPassAndMarkedWhileItIs()
    {
        var workspace = NewWorkspace();
        workspace = AddCallerWithOneCall(workspace);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, ["src/Notes.cs"]);

        // a second callsite in a caller that already had an edge: the new row is invisible until
        // Calc.cs is parsed again, and the edge it already had is what proves the lag
        WriteRepoFile(workspace, "src/Notes.cs", """
            namespace Demo;

            public static class Notes
            {
                public static int Term(Calc calc)
                {
                    var first = calc.Add(1, 2);
                    return first + calc.Add(3, 4);
                }
            }
            """);
        Index(workspace, pool, ["src/Notes.cs"]);

        var before = new RefLookup(workspace).Find("Add", null);
        Rows(before).ShouldContain("src/Notes.cs:7", "the edge the caller already had is what proves the lag");
        Rows(before).ShouldNotContain("src/Notes.cs:8", "the added callsite is invisible until the declaring side is re-parsed");
        before.LaggingDeclarations.ShouldNotBeNull();
        before.LaggingDeclarations.ShouldHaveSingleItem().File.ShouldBe("src/Calc.cs");

        Index(workspace, pool, []);

        var after = new RefLookup(workspace).Find("Add", null);
        Rows(after).ShouldContain("src/Notes.cs:8");
        after.LaggingDeclarations.ShouldBeNull();
    }

    [Fact]
    public void MeasureLag_OnAFreshIndex_CountsNothingAndReportsItsDenominator()
    {
        var workspace = NewWorkspace();
        new WorkspaceIndexer().Index(workspace);

        GhostRefLag lag;
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            lag = RefLookup.MeasureLag(store);
        }

        lag.ScannedRows.ShouldBeGreaterThan(0, "a zero denominator would make the zero count meaningless");
        lag.LaggingRows.ShouldBe(0);
        lag.OverdueDeclarations.ShouldBe(0);
        lag.HasLag.ShouldBeFalse();
    }

    [Fact]
    public void MeasureLag_CountsEachOverdueDeclaringFileOnce()
    {
        var workspace = NewWorkspace();
        new WorkspaceIndexer().Index(workspace);

        // two callers move past Calc.cs: one overdue declaring file, two lagging rows at least
        WriteRepoFile(workspace, "src/Runner.cs", "namespace Demo;\n\npublic class Runner\n{\n    public int Go() => 1;\n}\n");
        new WorkspaceIndexer().IndexFile(workspace, "demo", "src/Runner.cs");
        WriteRepoFile(workspace, "src/Other.cs", "namespace Demo;\n\npublic class Other\n{\n    public string Combine() => \"x\";\n}\n");
        new WorkspaceIndexer().IndexFile(workspace, "demo", "src/Other.cs");

        GhostRefLag lag;
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            lag = RefLookup.MeasureLag(store);
        }

        lag.OverdueDeclarations.ShouldBe(1);
        lag.LaggingRows.ShouldBeGreaterThanOrEqualTo(2);
        lag.ScannedRows.ShouldBeGreaterThan(lag.LaggingRows);
        lag.HasLag.ShouldBeTrue();
    }

    private static string AddCallerWithOneCall(string workspace)
    {
        WriteRepoFile(workspace, "src/Notes.cs", """
            namespace Demo;

            public static class Notes
            {
                public static int Term(Calc calc)
                {
                    return calc.Add(1, 2);
                }
            }
            """);
        return workspace;
    }

    private static List<string> Rows(RefLookupResult result) => RowsOf(result);

    private static List<string> RowsOf(RefLookupResult result) =>
        [.. result.Groups.SelectMany(g => g.Refs).Select(r => $"{r.File}:{r.Line}").Distinct()];

    private static void Index(string workspace, SemanticSessionPool pool, string[] changedRelativePaths, PhaseProgress? progress = null)
    {
        var changed = changedRelativePaths.Select(path => RepoFile(workspace, path)).ToList();
        new WorkspaceIndexer().Index(workspace, progress: progress, changedPaths: changed, sessions: pool);
    }

    private static string RepoRoot(string workspace) => Path.Combine(workspace, "repo");

    private static string RepoFile(string workspace, string relativePath) =>
        Path.Combine(RepoRoot(workspace), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteRepoFile(string workspace, string relativePath, string content) =>
        WriteFile(workspace, $"repo/{relativePath}", content);

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go(Calc calc)
                {
                    return calc.Add(1, 2);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Other.cs", """
            namespace Demo;

            public class Other
            {
                public int Also(Calc calc)
                {
                    return calc.Add(3, 4);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Notes.cs", """
            namespace Demo;

            public static class Notes
            {
                public static string Term = "pacedneedle";
            }
            """);
        WriteFile(workspace, "repo/src/Field.cs", """
            namespace Demo;

            public class Field
            {
                public int Value = 7;
            }
            """);
        WriteFile(workspace, "repo/src/Holder.cs", """
            namespace Demo;

            public class Holder
            {
                public Field? Cached;
            }
            """);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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
