using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpWorkspaceTests
{
    [Fact]
    public void RequireWorkspace_MissingManifest_ThrowsNotInitializedCode()
    {
        var root = TestDirs.NewTempDir();
        var workspace = new McpWorkspace(root, root, null);

        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);

        ex.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        ex.Code.ShouldNotBe(WorkspaceException.DefaultCode);
    }

    [Fact]
    public void RequireWorkspace_MissingManifest_KeepsTheHumanSentenceVerbatim()
    {
        var root = TestDirs.NewTempDir();
        var workspace = new McpWorkspace(root, root, null);

        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);

        ex.Message.ShouldBe("Workspace is not initialized. Run 'squeezr init' first.");
    }

    [Fact]
    public void RequireWorkspace_ManifestPresent_DoesNotThrow()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(SqueezrPaths.ManifestPath(root), "{}");

        Should.NotThrow(() => new McpWorkspace(root, root, null).RequireWorkspace());
    }

    [Fact]
    public void RequireWorkspace_SessionOutsideRoot_NamesBothPathsAndNeverSuggestsInit()
    {
        var workspace = new McpWorkspace(TestDirs.NewTempDir(), TestDirs.NewTempDir(), null);

        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);

        ex.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        ex.Message.ShouldContain($"'{workspace.Root}'");
        ex.Message.ShouldContain($"'{workspace.SessionDirectory}'");
        ex.Message.ShouldContain("Do not initialize a store");
        ex.Message.ShouldNotContain("squeezr init");
    }

    [Fact]
    public void RequireWorkspace_SessionInsideRoot_KeepsTheInitSentence()
    {
        var root = TestDirs.NewTempDir();
        var nested = Path.Combine(root, "src", "Squeezr.Core");
        Directory.CreateDirectory(nested);
        var workspace = new McpWorkspace(root, nested, null);

        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);

        ex.Message.ShouldBe("Workspace is not initialized. Run 'squeezr init' first.");
    }

    [Fact]
    public void SessionOutsideRoot_NestedSessionSitsInside()
    {
        var root = TestDirs.NewTempDir();
        var nested = Path.Combine(root, "deep");
        Directory.CreateDirectory(nested);

        new McpWorkspace(root, nested, null).SessionOutsideRoot.ShouldBeFalse();
        new McpWorkspace(root, root + Path.DirectorySeparatorChar, null).SessionOutsideRoot.ShouldBeFalse();
    }

    [Fact]
    public void SessionOutsideRoot_SiblingWithSharedPrefixIsOutside()
    {
        var parent = TestDirs.NewTempDir();
        var root = Path.Combine(parent, "ws");
        var sibling = Path.Combine(parent, "ws-other");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        new McpWorkspace(root, sibling, null).SessionOutsideRoot.ShouldBeTrue();
    }

    [Fact]
    public void SessionDirectory_IsKeptBesideTheRootForRemedyText()
    {
        var root = TestDirs.NewTempDir();
        var session = TestDirs.NewTempDir();

        var workspace = new McpWorkspace(root, session, null);

        workspace.Root.ShouldBe(Path.GetFullPath(root));
        workspace.SessionDirectory.ShouldBe(session);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("onnx")]
    [InlineData(null)]
    public void EmbeddingsProvider_IsTheCarriedStartupFact_NotASecondRead(string? provider)
    {
        var root = TestDirs.NewTempDir();

        var workspace = new McpWorkspace(root, root, provider);

        workspace.EmbeddingsProvider.ShouldBe(provider);
        File.Exists(SqueezrPaths.ConfigPath(root)).ShouldBeFalse();
    }
}
