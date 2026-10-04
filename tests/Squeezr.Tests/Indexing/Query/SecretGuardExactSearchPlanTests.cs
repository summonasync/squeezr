using Shouldly;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class SecretGuardExactSearchPlanTests
{
    [Fact]
    public void Execute_WithholdsProtectedCandidateBeforeRendering()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var hits = Run(workspace, SecretGuardFixture.EnvCanary);

        hits.ShouldNotContain(h => h.Match.Path == SecretGuardFixture.EnvPath);
    }

    [Fact]
    public void Execute_NamesNoProtectedPathInPlaceOfTheOrdinaryHit()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var hits = Run(workspace, SecretGuardFixture.AppCanary);

        hits.ShouldContain(h => h.Match.Path == SecretGuardFixture.AppPath);
        hits.ShouldNotContain(h => h.Match.Path == SecretGuardFixture.EnvPath);
    }

    [Fact]
    public void Execute_KeepsServingTheEnvCarveOut()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        Run(workspace, SecretGuardFixture.ExampleCanary)
            .Select(h => h.Match.Path)
            .ShouldContain(SecretGuardFixture.ExamplePath);
    }

    [Fact]
    public void Execute_WithholdsNestedSecretsFileAtDepth()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        Run(workspace, SecretGuardFixture.SecretsCanary).ShouldBeEmpty();
    }

    [Fact]
    public void Execute_ExtraPatternWithholdsTheFile()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        Run(workspace, SecretGuardFixture.TokensCanary).ShouldBeEmpty();
    }

    [Fact]
    public void Execute_ReloadsWidenedPatternsWithoutRebuildingTheIndex()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        Run(workspace, SecretGuardFixture.TokensCanary).ShouldNotBeEmpty();

        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        Run(workspace, SecretGuardFixture.TokensCanary).ShouldBeEmpty();
    }

    private static List<(SearchMatch Match, string? Key)> Run(string workspace, string literal) =>
        ExactSearchPlan.Execute(workspace, new SearchQuery(literal, false, null, null, null, null, null, null), trackChunkKeys: false);
}
