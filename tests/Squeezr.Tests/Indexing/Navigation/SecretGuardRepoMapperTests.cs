using Shouldly;
using Squeezr.Core.Indexing;
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
    public void Build_PrefixOverASubtreeOfOnlySecrets_ReportsWithheldInsteadOfAbsent()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 3, 4000, "deploy/");

        result.ScopedPrefix.ShouldBe("deploy/");
        result.Lines.Select(l => l.Text).ShouldNotContain("monkey.pem");
        result.Lines.ShouldNotContain(line => line.Text.StartsWith("keys/"));
        Withheld(result).ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('deploy/keys/monkey\.pem' matches built-in patterns\)$");
    }

    [Fact]
    public void Build_PrefixOverARepoOfOnlySecrets_MapsAnEmptySubtree()
    {
        var workspace = NewVaultWorkspace();

        var result = new RepoMapper(workspace).Build(null, 3, 4000, "deploy/");

        result.Lines.Select(l => l.Text).ShouldNotContain("monkey.pem");
        result.Repos.ShouldHaveSingleItem().ShouldBe("vault");
        Withheld(result).ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('deploy/keys/monkey\.pem' matches built-in patterns\)$");
    }

    [Fact]
    public void Build_UnscopedRepoOfOnlySecrets_MapsHeaderAndCountsEveryFile()
    {
        var workspace = NewVaultWorkspace();

        var result = new RepoMapper(workspace).Build(null, 3, 4000);

        result.Lines.Select(l => l.Text).ShouldNotContain("monkey.pem");
        result.Lines.Select(l => l.Text).ShouldNotContain(".env");
        Withheld(result).ShouldStartWith("secret guard: 2 result(s) withheld");
    }

    [Fact]
    public void Build_PrefixThatNamesAProtectedFile_RefusesInsteadOfReportingAbsent()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new RepoMapper(workspace).Build(null, 3, 4000, ".env"));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("No indexed files under prefix");
    }

    [Fact]
    public void Build_OrdinaryPrefix_StillScopes()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 2, 4000, "src/");

        result.Lines.Select(l => l.Text).ShouldContain("App.cs");
    }

    private static string Withheld(RepoMapResult result) =>
        result.GuardWarnings.ShouldNotBeNull()
            .Single(w => w.StartsWith("secret guard:", StringComparison.Ordinal));

    private static string NewVaultWorkspace()
    {
        var vault = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(vault, "vault"));
        var manager = new WorkspaceManager(vault);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(vault);
        manager.AddRepo("vault", "vault");
        foreach (var (relative, content) in new (string, string)[]
                 {
                     ("deploy/keys/monkey.pem", $"{SecretGuardFixture.PemCanary}\n"),
                     (".env", $"OPENAI_API_KEY={SecretGuardFixture.EnvCanary}\n"),
                 })
        {
            var fullPath = Path.Combine(vault, "vault", relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        new WorkspaceIndexer().Index(vault);
        return vault;
    }
}
