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
        Withheld(result).ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('\.env' matches built-in patterns\)$");
    }

    [Fact]
    public void Find_ProtectedHitAlongsideOrdinaryOnes_KeepsTheOrdinaryOnes()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "Run", "method");

        var result = new SymbolLookup(workspace).Find("Run", null, null, 500, body: true);

        result.Matches.ShouldHaveSingleItem().File.ShouldBe(SecretGuardFixture.AppPath);
        Withheld(result).ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('\.env' matches built-in patterns\)$");
    }

    [Fact]
    public void Find_ExtraPatternCoversTheFile_WithholdsTheHit()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var result = new SymbolLookup(workspace).Find("VaultToken", null, null, 500, body: true);

        result.Matches.ShouldBeEmpty();
        Withheld(result).ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('notes/tokens\.txt' matches indexing\.secretGuardExtra\)$");
    }

    [Fact]
    public void Find_SymbolInEnvCarveOut_StillRenders()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.ExamplePath, "OPENAI_API_KEY", "variable");

        var result = new SymbolLookup(workspace).Find("OPENAI_API_KEY", null, null, 500, body: true);

        result.Matches.ShouldHaveSingleItem().File.ShouldBe(SecretGuardFixture.ExamplePath);
        result.Matches[0].Body!.ShouldContain(SecretGuardFixture.ExampleCanary);
        result.GuardWarnings.ShouldBeNull();
    }

    [Fact]
    public void Find_NearNameDeclaredOnlyInProtectedFile_IsNotSuggested()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var result = new SymbolLookup(workspace).Find("VaultTokens", null, null, 500, body: false);

        (result.DidYouMean ?? []).ShouldNotContain("VaultToken");
    }

    [Fact]
    public void Find_NearNameInAnOrdinaryFile_IsStillSuggested()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");

        var result = new SymbolLookup(workspace).Find("VaultTokens", null, null, 500, body: false);

        result.DidYouMean.ShouldNotBeNull();
        result.DidYouMean.ShouldContain("VaultToken");
    }

    [Theory]
    [InlineData("OPENAI_API_KEY/")]
    [InlineData("/OPENAI_API_KEY")]
    [InlineData("OPENAI_API_KEY\\")]
    public void Find_SeparatorShapedName_DecidesOnStoredPathsOnlyAndInventsNoTally(string name)
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");

        var result = new SymbolLookup(workspace).Find(name, null, null, 500, body: true);

        result.Matches.ShouldBeEmpty();
        (result.DidYouMean ?? []).ShouldNotContain("OPENAI_API_KEY");
        result.GuardWarnings.ShouldBeNull();
    }

    private static string Withheld(SymbolLookupResult result) =>
        result.GuardWarnings.ShouldNotBeNull()
            .Single(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
}
