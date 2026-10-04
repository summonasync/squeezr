using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Roslyn;

public class PacedMsBuildSessionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void PacedPass_OverMsBuildWorkspace_IndexesEditedAndNewFilesWithoutReopening()
    {
        var workspace = NewWorkspaceOnFixture(out var restored);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, []);
        pool.CreatedSessions.ShouldBe(1);
        pool.IsWarm(restored, semantic: true).ShouldBeTrue();
        Symbols(workspace, "Calculator.cs").Select(s => s.Name).ShouldNotContain("Thrice");

        File.WriteAllText(
            Path.Combine(restored, "Calculator.cs"),
            File.ReadAllText(Path.Combine(restored, "Calculator.cs")) + """

                public class Thrice
                {
                    public static int Of(int value) => value * 3;
                }
                """);
        File.WriteAllText(Path.Combine(restored, "Added.cs"), """
            namespace Mini;

            public static class Added
            {
                public static int Use() => Thrice.Of(2);
            }
            """);
        var progress = new PhaseProgress();
        Index(workspace, pool, [Path.Combine(restored, "Calculator.cs"), Path.Combine(restored, "Added.cs")], progress);

        progress.Items(IndexPhase.Semantic).ShouldBe(["Added.cs", "Calculator.cs"]);
        pool.CreatedSessions.ShouldBe(1);
        Symbols(workspace, "Calculator.cs").Select(s => s.Name).ShouldContain("Thrice");
        Symbols(workspace, "Added.cs").Select(s => s.Name).ShouldContain("Use");
        var addedId = FileId(workspace, "Added.cs");
        RefRows(workspace, "Calculator.cs").ShouldContain(r => r.FileId == addedId);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string NewWorkspaceOnFixture(out string repoRoot)
    {
        repoRoot = CopyFixture("RoslynMini");
        RestoreProject(repoRoot);
        var manager = new WorkspaceManager(repoRoot);
        manager.Init(null);
        manager.AddRepo(".", "demo");
        return repoRoot;
    }

    private string CopyFixture(string name)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var dest = TestDirs.NewTempDir();
        _tempDirs.Add(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        return dest;
    }

    private static void RestoreProject(string dir)
    {
        var csproj = Directory.EnumerateFiles(dir, "*.csproj").Single();
        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{csproj}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(120_000).ShouldBeTrue();
        process.ExitCode.ShouldBe(0, process.StandardError.ReadToEnd());
    }

    private static void Index(string workspace, SemanticSessionPool pool, IReadOnlyList<string> changedPaths) =>
        new WorkspaceIndexer().Index(workspace, changedPaths: changedPaths, sessions: pool);

    private static void Index(
        string workspace,
        SemanticSessionPool pool,
        IReadOnlyList<string> changedPaths,
        PhaseProgress progress) =>
        new WorkspaceIndexer().Index(workspace, progress: progress, changedPaths: changedPaths, sessions: pool);

    private static uint FileId(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        return store.ListFiles(repoDoc.Id).Single(f => f.Path == relativePath).Id;
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
        private readonly List<(IndexPhase Phase, string Item)> _items = [];
        private IndexPhase _current;

        public IReadOnlyList<string> Items(IndexPhase phase) =>
            _items.Where(i => i.Phase == phase).Select(i => i.Item).Order().ToList();

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems) => _current = phase;

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null) =>
            _items.Add((_current, currentItem));

        public void RepoCompleted(string repoName)
        {
        }
    }
}
