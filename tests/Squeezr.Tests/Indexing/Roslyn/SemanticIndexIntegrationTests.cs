using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Roslyn;

public class SemanticIndexIntegrationTests
{
    [Fact]
    public void Index_WritesSymbolsChunksAndRefsToLiteDb()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);

        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var calcFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/Calc.cs");
        var useFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/Use.cs");

        var calcSymbols = store.ListSymbolsByFile(calcFile.Id);
        calcSymbols.Select(s => s.Name).ShouldContain("Calc");
        calcSymbols.Select(s => s.Name).ShouldContain("Add");
        var add = calcSymbols.Single(s => s.Name == "Add");
        add.Signature!.ShouldContain("Add(int a, int b)");
        add.DocComment!.ShouldContain("Adds numbers.");

        var chunks = store.ListChunksByFile(calcFile.Id);
        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.TokenCount > 0 && c.ChunkHash.Length == 64 && c.VectorStatus == "pending");
        chunks.ShouldContain(c => c.SymbolId == add.Id);

        var refs = store.ListSymbolsByFile(calcFile.Id)
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .ToList();
        refs.ShouldNotBeEmpty();
        refs.ShouldContain(r => r.Kind == "call" && r.FileId == useFile.Id && r.ToName!.Contains("Add"));

        var state = store.GetIndexState("demo")!;
        state.Stats.Symbols.ShouldBe(store.CountSymbols(repoDoc.Id));
        state.Stats.Symbols.ShouldBeGreaterThan(0);
        state.Stats.Chunks.ShouldBe(store.CountChunks(repoDoc.Id));
        state.Stats.Chunks.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void IncrementalIndex_UpdatesChangedFileAndRebuildsProjectRefs()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }
            }
            """);
        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Modified.ShouldBe(1);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var calcFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/Calc.cs");
        var names = store.ListSymbolsByFile(calcFile.Id).Select(s => s.Name).ToList();
        names.ShouldContain("Subtract");
        names.Count(s => s == "Add").ShouldBe(1);

        var refs = store.ListSymbolsByFile(calcFile.Id)
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .ToList();
        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.ToName != null);

        var state = store.GetIndexState("demo")!;
        state.Stats.Symbols.ShouldBe(store.CountSymbols(repoDoc.Id));
    }

    [Fact]
    public void DeletedFile_RemovesItsSymbolsChunksAndRefs()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        new WorkspaceIndexer().Index(workspace);

        File.Delete(Path.Combine(workspace, "repo", "src", "Use.cs"));
        var result = new WorkspaceIndexer().Index(workspace);

        result.Repos.ShouldHaveSingleItem().Deleted.ShouldBe(1);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        store.ListFiles(repoDoc.Id).Select(f => f.Path).ShouldBe(["src/Calc.cs"]);

        var calcFile = store.ListFiles(repoDoc.Id).Single(f => f.Path == "src/Calc.cs");
        var refs = store.ListSymbolsByFile(calcFile.Id)
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .ToList();
        refs.ShouldBeEmpty();
    }

    [Fact]
    public void NoSemanticIndex_WritesSymbolsButNoRefs_AndPurgesStaleRefsOnSwitch()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);

        new WorkspaceIndexer().Index(workspace, noSemantic: true);

        CountRefs(workspace).ShouldBe(0);
        CountSymbols(workspace).ShouldBeGreaterThan(0);

        new WorkspaceIndexer().Index(workspace);
        CountRefs(workspace).ShouldBeGreaterThan(0);

        new WorkspaceIndexer().Index(workspace, noSemantic: true);
        CountRefs(workspace).ShouldBe(0);
    }

    [Fact]
    public void BrokenFile_WarnsAndSkipsWhileRestOfRepoIndexes()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        WriteFile(workspace, "repo/src/garbage.cs", "??? not csharp at all\n");

        var result = new WorkspaceIndexer().Index(workspace);

        result.Warnings.ShouldContain(w => w.Contains("symbol extraction failed"));
        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Files.ShouldBe(3);
        repo.Skipped.ShouldBe(1);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        store.CountSymbols(repoDoc.Id).ShouldBeGreaterThan(0);
    }

    private static int CountSymbols(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        return store.CountSymbols(repoDoc.Id);
    }

    private static int CountRefs(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        return store.ListFiles(repoDoc.Id)
            .SelectMany(f => store.ListSymbolsByFile(f.Id))
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .Count();
    }

    private static string NewWorkspaceWithRepo()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteCalcAndUse(string workspace)
    {
        WriteFile(workspace, "repo/src/Calc.cs", """
            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Use.cs", """
            public static class Use
            {
                public static int Run()
                {
                    var c = new Calc();
                    return c.Add(1, 2);
                }
            }
            """);
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
