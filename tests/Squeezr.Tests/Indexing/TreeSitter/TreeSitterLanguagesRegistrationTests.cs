using Shouldly;
using Squeezr.Core.Indexing.TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Registration-only state: this class pins the line between 'known' and 'handled' that each language
// crosses on two separate days — go is still on the far side of it with no vocabulary at all, while
// python crossed both when its symbol table landed in T2 and its reference rules in T3.
public class TreeSitterLanguagesRegistrationTests
{
    // Two axes that grow on different days, so they get two theories: a language joins this first list
    // the day its symbol vocabulary lands (python in T2, go in T4) — routing is derived from that row —
    // and only later the second, when its reference rules land (python in T3, go in T5). A routed language
    // with no reference rules is a legal state — python sat in it for exactly one task — and the second
    // list is what says whether it still does
    [Theory]
    [InlineData(TreeSitterLanguages.JavaScript)]
    [InlineData(TreeSitterLanguages.TypeScript)]
    [InlineData(TreeSitterLanguages.Tsx)]
    [InlineData(TreeSitterLanguages.Rust)]
    [InlineData(TreeSitterLanguages.Python)]
    public void Routed_CarriesASymbolVocabulary(string language)
    {
        TreeSitterLanguages.Routed(language).ShouldBeTrue();
        TreeSitterLanguages.SymbolsFor(language).Kinds.Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(TreeSitterLanguages.JavaScript)]
    [InlineData(TreeSitterLanguages.TypeScript)]
    [InlineData(TreeSitterLanguages.Tsx)]
    [InlineData(TreeSitterLanguages.Rust)]
    [InlineData(TreeSitterLanguages.Python)]
    public void PoolBearingLanguage_CarriesAReferencePool(string language)
    {
        TreeSitterLanguages.ReferencesFor(language).ShouldNotBeNull();
        TreeSitterLanguages.ReferencePoolFor(language).ShouldNotBeNullOrEmpty();
    }

    // python joined the pool map on the day T3 promised, so the second axis is now live for it as well;
    // go is the only language still registered ahead of its vocabulary, and it is the row the fallback
    // tripwire in TreeSitterRoutingTests still guards
    [Theory]
    [InlineData(TreeSitterLanguages.Go)]
    public void RegisteredLanguage_IsNotRoutedAndCarriesNoRules(string language)
    {
        // the trap this pins: a routed file is taken off the fallback chunker, so routing a language
        // with an empty vocabulary would leave it with no symbols and no chunks at all
        TreeSitterLanguages.Routed(language).ShouldBeFalse();
        TreeSitterLanguages.SymbolsFor(language).Kinds.ShouldBeEmpty();
        TreeSitterLanguages.SymbolsFor(language).ContainerTypes.ShouldBeEmpty();
        TreeSitterLanguages.ReferencesFor(language).ShouldBeNull();
        TreeSitterLanguages.ReferencePoolFor(language).ShouldBeNull();
    }

    [Fact]
    public void PythonReferenceRules_LandedInItsOwnPoolNotASharedOne()
    {
        // the axis independence this file used to pin from the other side (routed, pool-less) is gone now
        // that both rows are filled — what stays load-bearing is WHICH pool: python's edges may only ever
        // match python files, because a pool shared with javascript would let 'mul' in a .js file bind a
        // python symbol of the same name
        TreeSitterLanguages.Routed(TreeSitterLanguages.Python).ShouldBeTrue();
        TreeSitterLanguages.SymbolsFor(TreeSitterLanguages.Python).Kinds.Count.ShouldBe(3);
        var rules = TreeSitterLanguages.ReferencesFor(TreeSitterLanguages.Python);
        rules.ShouldNotBeNull();
        rules!.Pool.ShouldBe(TreeSitterLanguages.PythonPool);
        rules.CallTypes.ShouldContain("call");
        rules.TypeTargetTypes.ShouldContain("class_definition");

        var pools = new[]
        {
            TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.JavaScript),
            TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.Rust),
            TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.Python),
        };
        pools.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void UnroutedLanguageAnswersTheSameAsAnUnknownOne()
    {
        // go carries both halves of the comparison now: python left the empty state when its tables landed
        TreeSitterLanguages.SymbolsFor(TreeSitterLanguages.Go).Kinds.Count
            .ShouldBe(TreeSitterLanguages.SymbolsFor("csharp").Kinds.Count);
        TreeSitterLanguages.ReferencesFor(TreeSitterLanguages.Go)
            .ShouldBe(TreeSitterLanguages.ReferencesFor("csharp"));
    }

    [Fact]
    public void PoolNamesAreDistinctPerGrammarAndReservedForPythonAndGo()
    {
        var pools = new[]
        {
            TreeSitterLanguages.JsTsPool,
            TreeSitterLanguages.RustPool,
            TreeSitterLanguages.PythonPool,
            TreeSitterLanguages.GoPool,
        };
        pools.Distinct().Count().ShouldBe(4);
        TreeSitterLanguages.PythonPool.ShouldBe(TreeSitterLanguages.Python);
        TreeSitterLanguages.GoPool.ShouldBe(TreeSitterLanguages.Go);
    }
}
