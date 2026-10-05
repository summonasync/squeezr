using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// One interface, one implementation, callsites through both spellings: the semantic capture hands
// both declarations the very same name-keyed reference list, which is what the read path must collapse.
internal static class RefDedupFixture
{
    internal const string Symbol = "RecordFailure";
    internal static readonly string[] Callsites = ["src/Guard.cs:7", "src/Guard.cs:8", "src/Reporter.cs:7"];

    internal static string NewInterfaceAndImplWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteMaterializerTree(workspace, "repo");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    // the reporter's own shape: a checkout and its worktree as two repositories of the same tree, so
    // every row renders the same relative path under two different repos
    internal static string NewMirroredWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteMaterializerTree(workspace, "alpha");
        WriteMaterializerTree(workspace, "beta");
        manager.AddRepo("alpha", "alpha");
        manager.AddRepo("beta", "beta");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteMaterializerTree(string workspace, string repoDir)
    {
        Write(workspace, $"{repoDir}/src/IMaterializer.cs", """
            namespace Demo;

            public interface IMaterializer
            {
                void RecordFailure(string id);
            }
            """);
        Write(workspace, $"{repoDir}/src/Materializer.cs", """
            namespace Demo;

            public class Materializer : IMaterializer
            {
                public void RecordFailure(string id)
                {
                }
            }
            """);
        Write(workspace, $"{repoDir}/src/Guard.cs", """
            namespace Demo;

            public class Guard
            {
                public void Try(IMaterializer viaInterface, Materializer viaConcrete)
                {
                    viaInterface.RecordFailure("a");
                    viaConcrete.RecordFailure("b");
                }
            }
            """);
        Write(workspace, $"{repoDir}/src/Reporter.cs", """
            namespace Demo;

            public class Reporter
            {
                public void Report(IMaterializer materializer)
                {
                    materializer.RecordFailure("c");
                }
            }
            """);
    }

    internal static string NewOverloadWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        Write(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        Write(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        Write(workspace, "repo/src/Other.cs", """
            namespace Demo;

            public class Other
            {
                public string Combine(Calc calc)
                {
                    return calc.Add("x", "y");
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    // same capture output the store would have produced twice for one location; the twin is picked by
    // file and line because two callsites can share a line number and ref order across passes is not stable
    internal static void PlantDuplicateRow(string workspace, string file, int line)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.ListRepos().First();
        var target = store.ListAllFiles().First(f => f.Path == file);
        var twin = store.ListSymbolsByRepo(repo.Id)
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .First(r => r.FileId == target.Id && r.Line == line);
        store.InsertRefs(
        [
            new RefDocument
            {
                FromSymbolId = twin.FromSymbolId,
                ToSymbolId = twin.ToSymbolId,
                ToName = twin.ToName,
                Kind = twin.Kind,
                FileId = twin.FileId,
                Line = twin.Line,
                EnclosingSymbol = twin.EnclosingSymbol,
            }
        ]);
    }

    // one extra row on one definition only: the two blocks now overlap but are not equal, which is the
    // case where the per-block render repeats a row and the flat lists collapse it
    internal static void PlantExtraRow(string workspace, string declFile, string file, int line)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.ListRepos().First();
        var symbol = store.ListSymbolsByRepo(repo.Id)
            .First(s => s.Name == Symbol && store.FindFileById(s.FileId)!.Path == declFile);
        var twin = store.ListRefsByFromSymbol(symbol.Id).First();
        var target = store.ListAllFiles().First(f => f.Path == file);
        store.InsertRefs(
        [
            new RefDocument
            {
                FromSymbolId = symbol.Id,
                ToSymbolId = twin.ToSymbolId,
                ToName = twin.ToName,
                Kind = twin.Kind,
                FileId = target.Id,
                Line = line,
                EnclosingSymbol = twin.EnclosingSymbol,
            }
        ]);
    }

    private static void Write(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
