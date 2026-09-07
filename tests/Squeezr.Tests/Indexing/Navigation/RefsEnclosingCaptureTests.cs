using LiteDB;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class RefsEnclosingCaptureTests
{
    [Fact]
    public void Refs_MethodRef_EnclosingSymbolIsContainerAndName()
    {
        var workspace = NewCalcWorkspace();

        var result = new RefLookup(workspace).Find("Add", null);

        var allRefs = result.Groups.SelectMany(g => g.Refs).ToList();
        allRefs.Count.ShouldBe(2);
        allRefs.Single(r => r.File == "src/Runner.cs").EnclosingSymbol.ShouldBe("Runner.Go");
        allRefs.Single(r => r.File == "src/Other.cs").EnclosingSymbol.ShouldBe("Other.Combine");
    }

    [Fact]
    public void Refs_FieldInitializerRef_EnclosingSymbolIsField()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        WriteFile(workspace, "repo/src/FieldHolder.cs", """
            namespace Demo;

            public class FieldHolder
            {
                private readonly Calc _calc = new Calc();
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var result = new RefLookup(workspace).Find("Calc", null);

        var refs = result.Groups.SelectMany(g => g.Refs).ToList();
        refs.ShouldContain(r => r.EnclosingSymbol == "FieldHolder._calc");
    }

    [Fact]
    public void Refs_RefInsidePartialClass_EnclosingSymbolResolvesAcrossParts()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo/src/PartA.cs", """
            namespace Demo;

            public partial class Split
            {
                public void MethodA()
                {
                    Target();
                }
            }
            """);
        WriteFile(workspace, "repo/src/PartB.cs", """
            namespace Demo;

            public partial class Split
            {
                public void Target()
                {
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var result = new RefLookup(workspace).Find("Target", null);

        var refs = result.Groups.SelectMany(g => g.Refs).ToList();
        refs.ShouldHaveSingleItem().EnclosingSymbol.ShouldBe("Split.MethodA");
    }

    [Fact]
    public void ListRefsByFromSymbol_LegacyDocWithoutEnclosingSymbol_ReturnsNull()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using (var db = new LiteDatabase(dbPath))
        {
            db.GetCollection(MetaStore.RefsCollection).Insert(new BsonDocument
            {
                ["_id"] = 1L,
                ["fromSymbolId"] = 42L,
                ["toSymbolId"] = BsonValue.Null,
                ["toName"] = "Add",
                ["kind"] = "call",
                ["fileId"] = 7,
                ["line"] = 3
            });
        }

        using var store = new MetaStore(dbPath);
        store.ListRefsByFromSymbol(42).ShouldHaveSingleItem().EnclosingSymbol.ShouldBeNull();
    }

    private static string NewCalcWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo/src/Calc.cs", """
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
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Other.cs", """
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

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
