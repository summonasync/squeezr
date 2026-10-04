using Shouldly;
using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Tests.Indexing.Navigation;

public class SecretGuardSymbolLookupTests
{
    [Fact]
    public void Find_SymbolDeclaredInProtectedFile_WithholdsTheHit()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");

        var result = new SymbolLookup(workspace).Find("OPENAI_API_KEY", null, null, 500, body: true);

        result.Matches.ShouldBeEmpty();
    }

    [Fact]
    public void Find_ProtectedHitAlongsideOrdinaryOnes_KeepsTheOrdinaryOnes()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "Run", "method");

        var result = new SymbolLookup(workspace).Find("Run", null, null, 500, body: true);

        result.Matches.ShouldHaveSingleItem().File.ShouldBe(SecretGuardFixture.AppPath);
    }

    [Fact]
    public void Find_ExtraPatternCoversTheFile_WithholdsTheHit()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var result = new SymbolLookup(workspace).Find("VaultToken", null, null, 500, body: true);

        result.Matches.ShouldBeEmpty();
    }

    [Fact]
    public void Find_SymbolInEnvCarveOut_StillRenders()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.ExamplePath, "OPENAI_API_KEY", "variable");

        var result = new SymbolLookup(workspace).Find("OPENAI_API_KEY", null, null, 500, body: true);

        result.Matches.ShouldHaveSingleItem().File.ShouldBe(SecretGuardFixture.ExamplePath);
        result.Matches[0].Body!.ShouldContain(SecretGuardFixture.ExampleCanary);
    }
}
