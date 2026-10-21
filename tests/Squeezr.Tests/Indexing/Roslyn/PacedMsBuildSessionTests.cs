using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Roslyn;

[Collection("MsBuildLocator")]
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

    [Fact]
    public void WarmPacedPass_ReusesThePooledSessionWithoutDisposingIt()
    {
        var workspace = NewWorkspaceOnFixture(out var repoRoot);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, []);
        pool.CreatedSessions.ShouldBe(1);

        WriteRepoFile(repoRoot, "Reused.cs", """
            namespace Mini;

            public static class Reused
            {
                public static int Reheat() => new Calculator().Add(1, 2);
            }
            """);
        Index(workspace, pool, [Path.Combine(repoRoot, "Reused.cs")]);

        AppendTo(repoRoot, "Calculator.cs", """

            public class Simmer
            {
                public static int Value => 7;
            }
            """);
        Index(workspace, pool, [Path.Combine(repoRoot, "Calculator.cs")]);

        pool.CreatedSessions.ShouldBe(1, "a warm pass must not buy another solution open");
        pool.IsWarm(repoRoot, semantic: true).ShouldBeTrue();

        var held = pool.Acquire(repoRoot, semantic: true, out var session, _ => { });
        held.ShouldBe(AcquireOutcome.Pooled, "the pool owns a cached session, so the caller must not dispose it");
        session.ShouldNotBeNull().DisposeAttempted.ShouldBeFalse(
            "the reused session has to be alive: a disposed workspace is not reuse");

        Symbols(workspace, "Reused.cs").Select(s => s.Name).ShouldContain("Reheat");
        Symbols(workspace, "Calculator.cs").Select(s => s.Name).ShouldContain("Simmer");
    }

    [Fact]
    public void WarmPacedPass_WithFileOutsideEveryProject_RunsTheReopenHealGate()
    {
        var workspace = NewWorkspaceWithProjectInSubdir(out var repoRoot);
        using var pool = new SemanticSessionPool();
        Index(workspace, pool, []);
        pool.CreatedSessions.ShouldBe(1);
        pool.IsWarm(repoRoot, semantic: true).ShouldBeTrue();

        // a .cs that no loaded project owns is the only reachable way to ask for a reopen: with the
        // project at the repo root every walked file falls under its directory and nothing is ever rejected
        WriteRepoFile(repoRoot, "Loose.cs", """
            public static class Loose
            {
                public static int Value => 1;
            }
            """);
        Index(workspace, pool, [Path.Combine(repoRoot, "Loose.cs")]);

        pool.IsWarm(repoRoot, semantic: true).ShouldBeFalse(
            "an unresolved project rejection on a warm pass must reach the reopen gate");
        pool.CreatedSessions.ShouldBe(1);

        Index(workspace, pool, []);
        pool.CreatedSessions.ShouldBe(2, "the healed pass reopens once and then stays warm");
        pool.IsWarm(repoRoot, semantic: true).ShouldBeTrue();

        // touch the same orphan again: the rejection is already remembered for this root, so the gate
        // must refuse a second reopen instead of buying a solution open every pass, and the warm
        // session has to walk out of that pass alive
        WriteRepoFile(repoRoot, "Loose.cs", """
            public static class Loose
            {
                public static int Value => 2;

                public static int Again => 3;
            }
            """);
        Index(workspace, pool, [Path.Combine(repoRoot, "Loose.cs")]);

        pool.CreatedSessions.ShouldBe(2, "a remembered rejection cannot keep buying reopens");
        pool.IsWarm(repoRoot, semantic: true).ShouldBeTrue();
        Symbols(workspace, "Loose.cs").Select(s => s.Name).ShouldContain("Again");
    }

    [Fact]
    public void PooledSession_DoubleDispose_IsHarmless()
    {
        var workspace = NewWorkspaceOnFixture(out var repoRoot);
        var pool = new SemanticSessionPool();
        Index(workspace, pool, []);

        var outcome = pool.Acquire(repoRoot, semantic: true, out var session, _ => { });
        session.ShouldNotBeNull();
        outcome.ShouldBe(AcquireOutcome.Pooled);

        Should.NotThrow(() => session!.Dispose());
        Should.NotThrow(() => session.Dispose());
        session.DisposeAttempted.ShouldBeTrue();

        Should.NotThrow(() => pool.Discard(repoRoot));
        Should.NotThrow(pool.Dispose);
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

    private string NewWorkspaceWithProjectInSubdir(out string repoRoot)
    {
        repoRoot = TestDirs.NewTempDir();
        _tempDirs.Add(repoRoot);
        WriteRepoFile(repoRoot, "src/Mini/Mini.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

            </Project>
            """);
        WriteRepoFile(repoRoot, "src/Mini/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        WriteRepoFile(repoRoot, "src/Mini/Use.cs", """
            public static class Use
            {
                public static int Run() => new Calc().Add(1, 2);
            }
            """);
        RestoreProject(repoRoot);
        var manager = new WorkspaceManager(repoRoot);
        manager.Init(null);
        manager.AddRepo(".", "demo");
        return repoRoot;
    }

    private static void WriteRepoFile(string repoRoot, string relativePath, string content)
    {
        var full = Path.Combine(repoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void AppendTo(string repoRoot, string relativePath, string content) =>
        File.AppendAllText(Path.Combine(repoRoot, relativePath), content);

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
        var csproj = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories).Single();
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
