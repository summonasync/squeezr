using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class IndexProgressTests
{
    [Fact]
    public void Index_ReportsPhasesInOrderPerRepo()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/src/Program.cs", "class Program { static void Main() { } }");
        WriteFile(workspace, "repo/lib/util.js", "function add(a, b) { return a + b; }");
        var progress = new RecordingProgress();

        new WorkspaceIndexer().Index(workspace, progress: progress);

        var phases = progress.Events.Where(e => e.StartsWith("phase:", StringComparison.Ordinal)).ToList();
        phases.ShouldContain("phase:demo:Walk:0");
        phases.ShouldContain("phase:demo:Files:2");
        phases.ShouldContain("phase:demo:Semantic:1");
        phases.ShouldContain("phase:demo:Trigram:2");

        var ordered = progress.Events
            .Select(e => e.StartsWith("phase:", StringComparison.Ordinal) ? e.Split(':')[2] : null)
            .Where(e => e is not null)
            .Distinct()
            .ToList();
        ordered.ShouldBe(["Walk", "Files", "Semantic", "Trigram"]);

        progress.Events.Last().ShouldBe("done:demo");
    }

    [Fact]
    public void Index_TicksEveryWalkedFile()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        WriteFile(workspace, "repo/b.cs", "class B { }");
        WriteFile(workspace, "repo/c.cs", "class C { }");
        var progress = new RecordingProgress();

        new WorkspaceIndexer().Index(workspace, progress: progress);

        var items = progress.Events
            .Where(e => e.StartsWith("item:", StringComparison.Ordinal))
            .Select(e => e.Split(':')[2])
            .ToList();
        items.ShouldContain("a.cs");
        items.ShouldContain("b.cs");
        items.ShouldContain("c.cs");
    }

    [Fact]
    public void IncrementalIndex_WithoutChanges_CompletesWithoutTrigramPhase()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new WorkspaceIndexer().Index(workspace);
        var progress = new RecordingProgress();

        new WorkspaceIndexer().Index(workspace, progress: progress);

        progress.Events.ShouldContain("phase:demo:Walk:0");
        progress.Events.ShouldContain("phase:demo:Files:1");
        progress.Events.ShouldNotContain(e => e.StartsWith("phase:demo:Trigram:", StringComparison.Ordinal));
        progress.Events.Last().ShouldBe("done:demo");
    }

    [Fact]
    public void Index_WithoutProgress_StillWorks()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");

        var result = new WorkspaceIndexer().Index(workspace);

        result.Repos.ShouldHaveSingleItem().Added.ShouldBe(1);
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class RecordingProgress : IIndexProgress
    {
        public List<string> Events { get; } = [];

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems) =>
            Events.Add($"phase:{repoName}:{phase}:{totalItems}");

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null) =>
            Events.Add($"item:{repoName}:{currentItem}");

        public void RepoCompleted(string repoName) =>
            Events.Add($"done:{repoName}");
    }
}
