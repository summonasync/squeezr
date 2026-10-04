using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing;

// A reader that could not reach config.json still holds the built-in half, so nothing in the answer says
// "I dropped your masks" unless every surface repeats it: an operator reading one tool's output has to be
// able to tell "no secrets here" from "the guard is half-asleep".
public class SecretGuardDegradedConfigTests
{
    [Fact]
    public void CodeSearch_ServesTheOrdinaryHitAndReportsDeadMasks()
    {
        var workspace = NewDegradedWorkspace();

        var response = new SearchEngine(workspace).Search(
            new SearchQuery(SecretGuardFixture.TokensCanary, false, null, null, null, null, null, null));

        response.Matches.Select(m => m.Path).ShouldContain(SecretGuardFixture.TokensPath);
        Degradation(response.Warnings).ShouldNotBeNull();
    }

    [Fact]
    public void RepoMap_ListsTheMaskedFileAndReportsDeadMasks()
    {
        var workspace = NewDegradedWorkspace();

        var result = new RepoMapper(workspace).Build(null, 3, 4000);

        result.Lines.Select(l => l.Text).ShouldContain("tokens.txt");
        Degradation(result.GuardWarnings).ShouldNotBeNull();
    }

    [Fact]
    public void SymbolLookup_RendersTheMaskedSymbolAndReportsDeadMasks()
    {
        var workspace = NewDegradedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");

        var result = new SymbolLookup(workspace).Find("VaultToken", null, null, 500, body: false);

        result.Matches.ShouldHaveSingleItem().File.ShouldBe(SecretGuardFixture.TokensPath);
        Degradation(result.GuardWarnings).ShouldNotBeNull();
    }

    [Fact]
    public void FindReferences_ServesTheMaskedFileAndReportsDeadMasks()
    {
        var workspace = NewDegradedWorkspace();

        var result = new RefLookup(workspace).Find("Greeting", null);

        result.Groups.SelectMany(g => g.Refs).ShouldNotBeEmpty();
        Degradation(result.GuardWarnings).ShouldNotBeNull();
    }

    [Fact]
    public void ExpandSpan_RendersTheMaskedFileAndReportsDeadMasks()
    {
        var workspace = NewDegradedWorkspace();

        var result = new SpanExpander(workspace).Expand(
            SecretGuardFixture.TokensPath, 1, null, 0, null, 500);

        result.Span.ShouldContain(SecretGuardFixture.TokensCanary);
        Degradation(result.GuardWarnings).ShouldNotBeNull();
    }

    [Fact]
    public void ExpandSpan_StillRefusesBuiltInSecretsWithADegradedReader()
    {
        var workspace = NewDegradedWorkspace();

        var ex = Should.Throw<Squeezr.Core.Workspace.WorkspaceException>(
            () => new SpanExpander(workspace).Expand(SecretGuardFixture.EnvPath, 1, null, 3, null, 500));

        ex.Message.ShouldContain("protected by squeezr's built-in secret guard");
    }

    private static string NewDegradedWorkspace()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        SecretGuardFixture.CorruptConfig(workspace);
        return workspace;
    }

    private static string? Degradation(IReadOnlyList<string>? warnings) =>
        warnings?.FirstOrDefault(
            w => w.StartsWith("could not read indexing config (", StringComparison.Ordinal));
}
