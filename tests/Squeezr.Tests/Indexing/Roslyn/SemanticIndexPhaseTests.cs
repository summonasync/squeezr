using Microsoft.CodeAnalysis;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Roslyn;

public class SemanticIndexPhaseTests
{
    [Fact]
    public void RunWithSession_SyntaxOnlySessionWithoutChanges_KeepsRefsAndSymbols()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var refsBefore = ListAllRefs(store, repoDoc.Id);
        var symbolsBefore = store.CountSymbols(repoDoc.Id);
        refsBefore.ShouldNotBeEmpty();

        RunSyntaxOnlyPhase(workspace, store, repoDoc, []);

        ListAllRefs(store, repoDoc.Id).Count.ShouldBe(refsBefore.Count);
        store.CountSymbols(repoDoc.Id).ShouldBe(symbolsBefore);
    }

    [Fact]
    public void RunWithSession_SyntaxOnlySessionWithChangedFile_RebuildsOnlyChangedFile()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/src/Use.cs", """
            public static class Use
            {
                public static int Run()
                {
                    var c = new Calc();
                    return c.Add(1, 2) + Extra();
                }

                public static int Extra()
                {
                    return 7;
                }
            }
            """);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var changedByPath = new Dictionary<string, FileDocument> { ["src/Use.cs"] = dbFilesByPath["src/Use.cs"] };

        RunSyntaxOnlyPhase(workspace, store, repoDoc, changedByPath);

        store.ListSymbolsByFile(dbFilesByPath["src/Use.cs"].Id)
            .Select(s => s.Name)
            .ShouldContain("Extra");

        var add = store.ListSymbolsByFile(dbFilesByPath["src/Calc.cs"].Id).Single(s => s.Name == "Add");
        var addRefs = store.ListRefsByFromSymbol(add.Id);
        addRefs.ShouldNotBeEmpty();
        addRefs.ShouldContain(r => r.FileId == dbFilesByPath["src/Use.cs"].Id && r.Kind == "call");
    }

    [Fact]
    public void RunWithSession_SemanticSessionWithDriftedSignature_ReprocessesTheUnchangedFile()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var calcFileId = dbFilesByPath["src/Calc.cs"].Id;
        var stored = store.ListSymbolsByFile(calcFileId).ToList();
        var names = stored.Select(s => s.Name).ToList();
        stored.Single(s => s.Name == "Add").Signature = "syntax-only stand-in";
        store.DeleteSymbolsByFile(calcFileId);
        store.InsertSymbols(stored);

        RunSemanticPhase(workspace, store, repoDoc, []);

        var reprocessed = store.ListSymbolsByFile(calcFileId).ToList();
        reprocessed.Select(s => s.Name).OrderBy(n => n).ShouldBe(names.OrderBy(n => n));
        reprocessed.Single(s => s.Name == "Add").Signature.ShouldNotBe("syntax-only stand-in");
        reprocessed.Single(s => s.Name == "Add").Signature!.ShouldContain("Add(int a, int b)");
    }

    [Fact]
    public void RunWithSession_SemanticSessionWithoutDrift_KeepsStoredRowsUntouched()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteCalcAndUse(workspace);
        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var calcFileId = dbFilesByPath["src/Calc.cs"].Id;
        var before = store.ListSymbolsByFile(calcFileId).ToDictionary(s => s.Name, StringComparer.Ordinal);

        RunSemanticPhase(workspace, store, repoDoc, []);

        var after = store.ListSymbolsByFile(calcFileId).ToDictionary(s => s.Name, StringComparer.Ordinal);
        after.Keys.OrderBy(k => k).ShouldBe(before.Keys.OrderBy(k => k));
        foreach (var (name, symbol) in before)
        {
            after[name].Id.ShouldBe(symbol.Id);
            after[name].Signature.ShouldBe(symbol.Signature);
        }
    }

    [Fact]
    public async Task InsertRefs_LinkedFileSharedByTwoProjects_StoresEachLocationOnce()
    {
        var workspace = NewWorkspaceWithRepo();
        WriteGreeterAndShared(workspace);
        new WorkspaceIndexer().Index(workspace);

        var repoRoot = Path.Combine(workspace, "repo");
        var greeterPath = Path.Combine(repoRoot, "src/Greeter.cs");
        var sharedPath = Path.Combine(repoRoot, "src/Shared.cs");
        var sharedSource = File.ReadAllText(sharedPath);

        var references = ReferenceExtractorTests.RuntimeReferences();
        using var adhoc = new AdhocWorkspace();
        var lib = adhoc.CurrentSolution
            .AddProject("Lib", "Lib", LanguageNames.CSharp)
            .AddMetadataReferences(references);
        var libDoc = lib.AddDocument("Greeter.cs", File.ReadAllText(greeterPath), filePath: greeterPath);
        var app1 = libDoc.Project.Solution
            .AddProject("App1", "App1", LanguageNames.CSharp)
            .AddMetadataReferences(references)
            .AddProjectReference(new ProjectReference(lib.Id));
        var app1Shared = app1.AddDocument("Shared.cs", sharedSource, filePath: sharedPath);
        var app2 = app1Shared.Project.Solution
            .AddProject("App2", "App2", LanguageNames.CSharp)
            .AddMetadataReferences(references)
            .AddProjectReference(new ProjectReference(lib.Id));
        var app2Shared = app2.AddDocument("Shared.cs", sharedSource, filePath: sharedPath);

        var currentLibDoc = app2Shared.Project.Solution.GetDocument(libDoc.Id)!;
        var model = await currentLibDoc.GetSemanticModelAsync(CancellationToken.None);
        var root = await currentLibDoc.GetSyntaxRootAsync(CancellationToken.None);
        var declared = SymbolExtractor.Extract(root!, model!, File.ReadAllText(greeterPath));

        var refs = await ReferenceExtractor.FindAsync(declared, currentLibDoc, CancellationToken.None);
        refs.Count(r => r.FromPath == sharedPath && r.ToName == "Greeter").ShouldBe(2);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var greeterSymbols = store.ListSymbolsByFile(dbFilesByPath["src/Greeter.cs"].Id).ToList();
        store.DeleteRefsByFromSymbolIds(greeterSymbols.Select(s => s.Id).ToList());

        SemanticIndexPhase.InsertRefs(
            new ParseResult("csharp", declared.Select(d => d.Symbol).ToList(), refs, []),
            greeterSymbols,
            repoRoot,
            dbFilesByPath,
            store);

        var storedRefs = greeterSymbols
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .Where(r => r.FileId == dbFilesByPath["src/Shared.cs"].Id)
            .ToList();
        storedRefs.Count.ShouldBe(2);
        storedRefs.ShouldContain(r => r.Kind == "reference");
        storedRefs.ShouldContain(r => r.Kind == "call");
    }

    private static void RunSyntaxOnlyPhase(
        string workspace,
        MetaStore store,
        RepoDocument repoDoc,
        Dictionary<string, FileDocument> changedByPath)
    {
        var repoRoot = Path.Combine(workspace, "repo");
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var csFiles = dbFilesByPath.Values
            .Select(f => new SourceFile(
                f.Path,
                Path.Combine(repoRoot, f.Path.Replace('/', Path.DirectorySeparatorChar)),
                "csharp",
                f.SizeBytes,
                f.Sha256))
            .ToList();

        using var session = RoslynSession.Create(repoRoot, semantic: false);
        session.Semantic.ShouldBeFalse();

        SemanticIndexPhase.RunWithSession(
            session, repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath,
            SqueezrConfig.CreateDefault(), noSemantic: false, SecretGuardIngestion.Load(workspace), _ => { });
    }

    private static void RunSemanticPhase(
        string workspace,
        MetaStore store,
        RepoDocument repoDoc,
        Dictionary<string, FileDocument> changedByPath)
    {
        var repoRoot = Path.Combine(workspace, "repo");
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var csFiles = dbFilesByPath.Values
            .Select(f => new SourceFile(
                f.Path,
                Path.Combine(repoRoot, f.Path.Replace('/', Path.DirectorySeparatorChar)),
                "csharp",
                f.SizeBytes,
                f.Sha256))
            .ToList();

        using var session = RoslynSession.Create(repoRoot, semantic: true);
        session.Semantic.ShouldBeTrue();

        SemanticIndexPhase.RunWithSession(
            session, repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath,
            SqueezrConfig.CreateDefault(), noSemantic: false, SecretGuardIngestion.Load(workspace), _ => { });
    }

    private static List<RefDocument> ListAllRefs(MetaStore store, uint repoId) =>
        store.ListFiles(repoId)
            .SelectMany(f => store.ListSymbolsByFile(f.Id))
            .SelectMany(s => store.ListRefsByFromSymbol(s.Id))
            .ToList();

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

    private static void WriteGreeterAndShared(string workspace)
    {
        WriteFile(workspace, "repo/src/Greeter.cs", """
            namespace Lib;

            public class Greeter
            {
                public string Hello() => "hi";
            }
            """);
        WriteFile(workspace, "repo/src/Shared.cs", """
            using Lib;

            namespace App;

            public static class Shared
            {
                public static string Run()
                {
                    var greeter = new Greeter();
                    return greeter.Hello();
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
