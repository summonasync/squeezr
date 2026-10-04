using Shouldly;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class SecretGuardRepoMapperTests
{
    [Fact]
    public void Build_LeavesProtectedPathsOutOfTheTree()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var lines = new RepoMapper(workspace).Build(null, 3, 4000).Lines.Select(l => l.Text).ToList();

        lines.ShouldNotContain(".env");
        lines.ShouldNotContain("monkey.pem");
        lines.ShouldNotContain("secrets.json");
        lines.ShouldContain(SecretGuardFixture.ExamplePath.Split('/')[^1]);
        lines.ShouldContain("App.cs");
    }

    [Fact]
    public void Build_DropsDirectoryCountersBuiltFromProtectedFiles()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var lines = new RepoMapper(workspace).Build(null, 2, 4000).Lines.ToList();

        lines.ShouldNotContain(line => line.Text.StartsWith("keys/"));
        lines.ShouldNotContain(line => line.Text.StartsWith("config/"));
    }

    [Fact]
    public void Build_SkipsSymbolSignaturesOfProtectedFiles()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");

        var lines = new RepoMapper(workspace).Build(null, 3, 4000).Lines.Select(l => l.Text).ToList();

        lines.ShouldNotContain("variable OPENAI_API_KEY");
    }

    [Fact]
    public void Build_ExtraPatternHidesTheFileAndItsDirectory()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var lines = new RepoMapper(workspace).Build(null, 3, 4000).Lines;

        lines.Select(l => l.Text).ShouldNotContain("tokens.txt");
        lines.ShouldNotContain(line => line.Text.StartsWith("notes/"));
    }

    [Fact]
    public void Build_PrefixOverASubtreeOfOnlySecrets_RefusesWithoutNamingTheFile()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new RepoMapper(workspace).Build(null, 3, 4000, "deploy/"));

        ex.Message.ShouldNotContain("monkey.pem");
        ex.Message.ShouldNotContain("keys/ (");
    }

    [Fact]
    public void Build_OrdinaryPrefix_StillScopes()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 2, 4000, "src/");

        result.Lines.Select(l => l.Text).ShouldContain("App.cs");
    }
}
