using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class SecretGuardSearchEngineTests
{
    [Fact]
    public void Search_ExactArmWithholdsProtectedPathAndCountsIt()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var response = new SearchEngine(workspace).Search(Literal(SecretGuardFixture.EnvCanary));

        response.Matches.ShouldBeEmpty();
        Withheld(response.Warnings)!.ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('\.env' matches built-in patterns\)$");
    }

    [Fact]
    public void Search_WarningNamesThePathButNeverTheValue()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var response = new SearchEngine(workspace).Search(Literal(SecretGuardFixture.EnvCanary));

        string.Join("|", response.Warnings).ShouldNotContain(SecretGuardFixture.EnvCanary);
    }

    [Fact]
    public void Search_CountsEveryWithheldResultAndKeepsTheServedOnes()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var response = new SearchEngine(workspace).Search(Literal("CANARY"));

        response.Matches.Select(m => m.Path).ShouldBe(["notes/tokens.txt"]);
        Withheld(response.Warnings)!.ShouldMatch(
            """
            ^secret guard: 3 result\(s\) withheld \('(?:\.env|config/secrets\.json|deploy/keys/monkey\.pem)' matches built-in patterns\)$
            """);
    }

    [Fact]
    public void Search_KeepsOrdinaryHitsAndStaysSilentAboutTheGuard()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var response = new SearchEngine(workspace).Search(Literal(SecretGuardFixture.AppCanary));

        response.Matches.ShouldNotBeEmpty();
        Withheld(response.Warnings).ShouldBeNull();
    }

    [Fact]
    public void Search_StillServesTheEnvCarveOut_WithTheSecretValueMasked()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var response = new SearchEngine(workspace).Search(Literal("OPENAI_API_KEY"));

        var match = response.Matches.First(m => m.Path == SecretGuardFixture.ExamplePath);
        match.Snippet.ShouldContain(SecretSnippetRedactor.Masked);
        match.Snippet.ShouldNotContain(SecretGuardFixture.ExampleCanary);
        Withheld(response.Warnings)!.ShouldContain("'.env' matches built-in patterns");
    }

    [Fact]
    public async Task SearchHybrid_SemanticArmAloneCannotSurfaceAProtectedPath()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var chunk = SecretGuardFixture.ChunkHolding(workspace, SecretGuardFixture.EnvPath, SecretGuardFixture.EnvCanary);
        var store = new SecretGuardFixture.ScriptedVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, chunk.RepoId, chunk.StartLine, chunk.EndLine, 0.99)]
        };

        var response = await new SearchEngine(workspace).SearchHybridAsync(
            Literal("zznotaword"), TestVectorPhase.Arm(workspace, new SecretGuardFixture.FixedEmbeddings(), store), CancellationToken.None);

        response.Matches.ShouldBeEmpty();
        Withheld(response.Warnings)!.ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('\.env' matches built-in patterns\)$");
    }

    [Fact]
    public void Search_ExtraPatternIsCountedAndNamedAsTheConfigSource()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var response = new SearchEngine(workspace).Search(Literal(SecretGuardFixture.TokensCanary));

        response.Matches.ShouldBeEmpty();
        Withheld(response.Warnings)!.ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('notes/tokens\.txt' matches indexing\.secretGuardExtra\)$");
    }

    [Fact]
    public void Search_PrefixScopedToAProtectedSubtreeWithholdsInsteadOfServing()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(Literal("CANARY") with { Prefix = "deploy/keys" });

        response.Matches.ShouldBeEmpty();
        Withheld(response.Warnings)!.ShouldMatch(
            @"^secret guard: 1 result\(s\) withheld \('deploy/keys/monkey\.pem' matches built-in patterns\)$");
    }

    [Fact]
    public void Search_PrefixNamingAProtectedFile_RefusesInsteadOfReportingAbsent()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();

        var ex = Should.Throw<WorkspaceException>(
            () => new SearchEngine(workspace).Search(Literal("CANARY") with { Prefix = ".env" }));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
        ex.Message.ShouldNotContain("No indexed files under prefix");
    }

    [Fact]
    public void Search_PrefixThatMatchesNothingNeverNamesAProtectedDirectoryInTheSuggestion()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var ex = Should.Throw<WorkspaceException>(() => engine.Search(Literal("CANARY") with { Prefix = "keys" }));

        ex.Message.ShouldNotContain("deploy/");
        ex.Message.ShouldNotContain("config/");
        ex.Message.ShouldNotContain("monkey.pem");
    }

    private static SearchQuery Literal(string text) =>
        new(text, false, null, null, null, null, null, null);

    private static string? Withheld(IReadOnlyList<string> warnings) =>
        warnings.FirstOrDefault(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
}
