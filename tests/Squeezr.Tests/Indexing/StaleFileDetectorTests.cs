using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public sealed class StaleFileDetectorTests
{
    [Fact]
    public void Detect_AfterCleanIndex_ReturnsEmpty()
    {
        var workspace = NewIndexedWorkspace(out _);

        StaleFileDetector.Detect(workspace).ShouldBeEmpty();
    }

    [Fact]
    public void Detect_ModifiedFile_IsStale()
    {
        var workspace = NewIndexedWorkspace(out _);
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "Calc.cs"), "public class Calc { public int Twice(int x) => x * 2; }\n");

        StaleFileDetector.Detect(workspace).ShouldBe(["repo/src/Calc.cs"]);
    }

    [Fact]
    public void Detect_NewFileOnDisk_IsStale()
    {
        var workspace = NewIndexedWorkspace(out _);
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "New.cs"), "public class New { }\n");

        StaleFileDetector.Detect(workspace).ShouldBe(["repo/src/New.cs"]);
    }

    [Fact]
    public void Detect_DeletedFile_IsStale()
    {
        var workspace = NewIndexedWorkspace(out _);
        File.Delete(Path.Combine(workspace, "repo", "src", "Calc.cs"));

        StaleFileDetector.Detect(workspace).ShouldBe(["repo/src/Calc.cs"]);
    }

    [Fact]
    public void Detect_UnindexedRepo_FilesAreStale()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo/src/Calc.cs", "public class Calc { }\n");
        manager.AddRepo("repo", "repo");

        StaleFileDetector.Detect(workspace).ShouldBe(["repo/src/Calc.cs"]);
    }

    [Fact]
    public void Detect_MissingRepoDir_IndexedFilesAreStale()
    {
        var workspace = NewIndexedWorkspace(out _);
        Directory.Delete(Path.Combine(workspace, "repo"), recursive: true);

        StaleFileDetector.Detect(workspace).ShouldBe(["repo/src/Calc.cs"]);
    }

    [Fact]
    public void Detect_UninitializedWorkspace_Throws()
    {
        var workspace = TestDirs.NewTempDir();

        var ex = Should.Throw<WorkspaceException>(() => StaleFileDetector.Detect(workspace));
        ex.Code.ShouldBe(WorkspaceException.NotInitializedCode);
    }

    private static string NewIndexedWorkspace(out WorkspaceManager manager)
    {
        var workspace = TestDirs.NewTempDir();
        manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "repo/src/Calc.cs", "public class Calc { public int Add(int a, int b) => a + b; }\n");
        manager.AddRepo("repo", "repo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
