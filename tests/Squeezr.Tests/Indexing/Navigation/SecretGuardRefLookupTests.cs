using Shouldly;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class SecretGuardRefLookupTests
{
    [Fact]
    public void Find_WithholdsReferencesThatSitInsideAProtectedFile()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.EnvPath, 1);

        var result = new RefLookup(workspace).Find("Run", null);

        result.Groups.ShouldNotBeEmpty();
        result.Groups.SelectMany(g => g.Refs).ShouldNotContain(r => r.File == SecretGuardFixture.EnvPath);
    }

    [Fact]
    public void Find_SymbolDeclaredOnlyInProtectedFiles_RefusesInsteadOfReportingAbsent()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "DeployKey", "variable");

        var ex = Should.Throw<WorkspaceException>(() => new RefLookup(workspace).Find("DeployKey", null));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("No symbol named");
    }

    [Fact]
    public void Find_ExtraPatternFile_WithholdsItsReferences()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.TokensPath, 1);
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var result = new RefLookup(workspace).Find("Run", null);

        result.Groups.SelectMany(g => g.Refs).ShouldNotContain(r => r.File == SecretGuardFixture.TokensPath);
    }

    [Fact]
    public void Find_OrdinaryReferences_StillComeThrough()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var result = new RefLookup(workspace).Find("Greeting", null);

        result.Groups.ShouldNotBeEmpty();
        result.Groups.SelectMany(g => g.Refs).ShouldContain(r => r.File == SecretGuardFixture.AppPath);
    }
}
